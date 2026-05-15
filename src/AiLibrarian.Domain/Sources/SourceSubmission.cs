namespace AiLibrarian.Domain.Sources;

/// <summary>
/// Inputs needed to INSERT a new <see cref="Source"/> row at submission
/// time. The portal upload endpoint and the MCP <c>submit_source</c>
/// tool (Phase 1+) build one of these from the caller's form data;
/// <c>ISourceWriter.CreateAsync</c> stamps the audit row and returns
/// the new source identifier.
///
/// <para>
/// Per-source SHA-256 and byte size are <b>not</b> part of submission —
/// the ingest worker computes them after canonicalization and updates
/// the row through <c>ISourceWriter.UpdateChecksumAndSizeAsync</c>.
/// Splitting the lifecycle this way keeps the upload path fast and
/// lets the worker be the single source of truth for derived metadata.
/// </para>
/// </summary>
/// <param name="DepartmentId">Owning department (corpus boundary).</param>
/// <param name="Classification">Default access boundary; defaults to
/// <see cref="Classification.Internal"/> per ADR 0011 when the upload
/// form leaves it blank.</param>
/// <param name="Title">Human-readable title; falls back to the file
/// name when the caller doesn't supply one.</param>
/// <param name="ContentType">MIME type. Required so the ingest worker
/// can resolve the right Skill plugin without re-sniffing bytes.</param>
/// <param name="Uri">Optional canonical URI (e.g. blob URL).</param>
/// <param name="ContributedBy">User OID that submitted the source —
/// recorded in <c>sources.contributed_by</c> regardless of whether RLS
/// would have blocked the user from reading the resulting row.</param>
public sealed record SourceSubmission(
	Guid DepartmentId,
	Classification Classification,
	string Title,
	string ContentType,
	string? Uri,
	Guid ContributedBy);
