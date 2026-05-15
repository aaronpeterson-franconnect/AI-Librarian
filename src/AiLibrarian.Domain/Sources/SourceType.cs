namespace AiLibrarian.Domain.Sources;

/// <summary>
/// Persona-level source taxonomy per ADR 0015 §"Retrieval-profile
/// shape". A string enum (not a C# enum) because the persona profile
/// JSONB references values as strings and tolerating forward
/// evolution matters more than compile-time enum safety here.
///
/// <para>Stored on <c>sources.source_type</c> (migration 0028). The
/// DB check constraint enforces this vocabulary; the
/// <see cref="SourceTypeClassifier"/> produces values consistent with
/// it.</para>
/// </summary>
public static class SourceType
{
	/// <summary>Source code files (Python, C#, Go, JavaScript, etc.).</summary>
	public const string Code = "code";

	/// <summary>SQL scripts, migrations, query snippets.</summary>
	public const string Sql = "sql";

	/// <summary>Operational runbooks, post-mortems, incident reviews.</summary>
	public const string Runbook = "runbook";

	/// <summary>Support / engineering ticket exports (Jira, GitHub Issues, ServiceNow).</summary>
	public const string Ticket = "ticket";

	/// <summary>Meeting transcripts, call notes, recorded discussion artifacts.</summary>
	public const string MeetingTranscript = "meeting_transcript";

	/// <summary>Internal wiki pages (Confluence exports, prior-system wikis).</summary>
	public const string WikiPage = "wiki_page";

	/// <summary>Email exports (mailbox archives, customer correspondence).</summary>
	public const string Email = "email";

	/// <summary>Image-only sources where the embedding represents OCR or alt-text rather than free-form prose.</summary>
	public const string Image = "image";

	/// <summary>Generic document — the fallback for content that doesn't match a more specific category.</summary>
	public const string Document = "document";

	/// <summary>Every documented value, in case callers want to enumerate.</summary>
	public static readonly IReadOnlyList<string> All =
	[
		Code, Sql, Runbook, Ticket, MeetingTranscript,
		WikiPage, Email, Image, Document,
	];
}

/// <summary>
/// Pure deterministic classifier: given a content-type / filename /
/// title, produce a persona-level <see cref="SourceType"/>. No I/O,
/// no LLM — just a rule cascade so ingestion can stamp the column at
/// upload time. The classifier is conservative: when no rule fires
/// the result is <see cref="SourceType.Document"/> rather than null,
/// so the database column never carries an unclassified row from
/// post-classifier ingestion.
///
/// <para>Order matters. Rules higher up the cascade take precedence
/// (e.g. a filename ending in <c>.sql</c> classifies as <c>sql</c>
/// even if the content-type is <c>text/plain</c>). Add new rules
/// near the top so they shadow the defaults rather than the other
/// way around.</para>
/// </summary>
public static class SourceTypeClassifier
{
	private static readonly string[] CodeExtensions =
	[
		".cs", ".py", ".js", ".ts", ".jsx", ".tsx", ".go", ".java", ".kt",
		".rb", ".rs", ".cpp", ".c", ".h", ".hpp", ".swift", ".scala",
		".php", ".sh", ".ps1", ".bash",
	];

	private static readonly string[] SqlExtensions = [".sql", ".psql"];

	private static readonly string[] ImageExtensions =
	[
		".png", ".jpg", ".jpeg", ".gif", ".webp", ".tiff", ".tif", ".bmp", ".svg",
	];

	private static readonly string[] EmailExtensions = [".eml", ".msg", ".mbox"];

	private static readonly string[] RunbookKeywords =
	[
		"runbook", "post-mortem", "postmortem", "post_mortem",
		"incident review", "incident-review", "rca",
	];

	private static readonly string[] TicketKeywords =
	[
		"ticket", "jira-", "issue-", "incident-",
	];

	private static readonly string[] MeetingKeywords =
	[
		"meeting", "transcript", "call notes", "standup", "stand-up",
	];

	private static readonly string[] WikiPageKeywords =
	[
		"wiki", "confluence",
	];

	private static readonly System.Buffers.SearchValues<char> PathSeparators
		= System.Buffers.SearchValues.Create("/\\");

	/// <summary>
	/// Classify a source. All arguments are tolerant: nulls and empties
	/// just contribute zero signal. The function is deterministic and
	/// allocation-light enough to call inline from ingestion.
	/// </summary>
	/// <param name="contentType">MIME type as reported by upload / fetch.</param>
	/// <param name="fileName">Original file name (may include path; the extension is what matters).</param>
	/// <param name="title">Operator-supplied or auto-extracted title.</param>
	public static string Classify(string? contentType, string? fileName, string? title)
	{
		var ext = ExtensionOf(fileName);
		var lowerTitle = title?.ToLowerInvariant() ?? string.Empty;
		var lowerContent = contentType?.ToLowerInvariant() ?? string.Empty;
		// fileName-derived keyword surface: the basename without extension,
		// lowercased. Operators upload files with descriptive names
		// (e.g. "post-mortem-2026-05.md") more often than they fill in
		// the title field, so the keyword cascade has to see both.
		var lowerNameStem = NameStem(fileName);

		// File-extension cascade. Extensions are the strongest signal
		// because operators control them; content-type can be wrong on
		// uploads.
		if (ext.Length > 0)
		{
			if (Array.IndexOf(SqlExtensions, ext) >= 0)
			{
				return SourceType.Sql;
			}
			if (Array.IndexOf(CodeExtensions, ext) >= 0)
			{
				return SourceType.Code;
			}
			if (Array.IndexOf(ImageExtensions, ext) >= 0)
			{
				return SourceType.Image;
			}
			if (Array.IndexOf(EmailExtensions, ext) >= 0)
			{
				return SourceType.Email;
			}
		}

		// Content-type cascade. Image types catch uploads with no
		// extension; text/x-* covers source-control checkouts.
		if (lowerContent.StartsWith("image/", StringComparison.Ordinal))
		{
			return SourceType.Image;
		}
		if (lowerContent.StartsWith("text/x-", StringComparison.Ordinal)
			|| lowerContent.Equals("application/x-sh", StringComparison.Ordinal)
			|| lowerContent.Equals("application/javascript", StringComparison.Ordinal)
			|| lowerContent.Equals("application/typescript", StringComparison.Ordinal))
		{
			return SourceType.Code;
		}
		if (lowerContent.Equals("message/rfc822", StringComparison.Ordinal)
			|| lowerContent.Equals("application/vnd.ms-outlook", StringComparison.Ordinal))
		{
			return SourceType.Email;
		}

		// Keyword cascade. Runs after the structural signals so a
		// misnamed ".sql" file still classifies as sql rather than
		// runbook. Checks both the operator-supplied title AND the
		// filename's basename — operators often skip the title field
		// but pick descriptive filenames like "post-mortem-2026-05.md".
		if (ContainsAny(lowerTitle, RunbookKeywords) || ContainsAny(lowerNameStem, RunbookKeywords))
		{
			return SourceType.Runbook;
		}
		if (ContainsAny(lowerTitle, TicketKeywords) || ContainsAny(lowerNameStem, TicketKeywords))
		{
			return SourceType.Ticket;
		}
		if (ContainsAny(lowerTitle, MeetingKeywords) || ContainsAny(lowerNameStem, MeetingKeywords))
		{
			return SourceType.MeetingTranscript;
		}
		if (ContainsAny(lowerTitle, WikiPageKeywords) || ContainsAny(lowerNameStem, WikiPageKeywords))
		{
			return SourceType.WikiPage;
		}

		// Default fallback.
		return SourceType.Document;
	}

	private static string ExtensionOf(string? fileName)
	{
		if (string.IsNullOrEmpty(fileName))
		{
			return string.Empty;
		}
		var dot = fileName.LastIndexOf('.');
		return dot < 0 ? string.Empty : fileName[dot..].ToLowerInvariant();
	}

	/// <summary>
	/// Strip any directory prefix + the extension off <paramref name="fileName"/>
	/// and lowercase the result. Used by the keyword cascade so
	/// filenames like <c>docs/runbooks/post-mortem-2026-05.md</c>
	/// match the "post-mortem" keyword.
	/// </summary>
	private static string NameStem(string? fileName)
	{
		if (string.IsNullOrEmpty(fileName))
		{
			return string.Empty;
		}
		// Strip path: use whichever separator the OS / source put in.
		var lastSep = fileName.AsSpan().LastIndexOfAny(PathSeparators);
		var basename = lastSep < 0 ? fileName : fileName[(lastSep + 1)..];
		var dot = basename.LastIndexOf('.');
		var stem = dot < 0 ? basename : basename[..dot];
		return stem.ToLowerInvariant();
	}

	private static bool ContainsAny(string haystack, string[] needles)
	{
		if (haystack.Length == 0)
		{
			return false;
		}
		foreach (var n in needles)
		{
			if (haystack.Contains(n, StringComparison.Ordinal))
			{
				return true;
			}
		}
		return false;
	}
}
