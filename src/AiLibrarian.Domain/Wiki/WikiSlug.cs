using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AiLibrarian.Domain.Wiki;

/// <summary>
/// Mirrors the <c>wiki_pages.slug</c> check constraint
/// (<c>^[a-z0-9][a-z0-9\-]{0,254}$</c>) from Liquibase <c>0020</c>.
/// The DB constraint is the source of truth; this helper produces
/// slugs the constraint accepts and validates ones the operator
/// supplied directly.
///
/// <para>The auto-page-discovery endpoint accepts either:</para>
/// <list type="bullet">
///   <item>An explicit slug (validated via <see cref="IsValid(string)"/>).</item>
///   <item>A title only (we derive a slug via <see cref="From(string)"/>).</item>
/// </list>
/// </summary>
public static partial class WikiSlug
{
	/// <summary>Maximum slug length per the DB check constraint.</summary>
	public const int MaxLength = 255;

	[GeneratedRegex("^[a-z0-9][a-z0-9\\-]{0,254}$")]
	private static partial Regex ValidatorRegex();

	[GeneratedRegex("[^a-z0-9]+")]
	private static partial Regex NonAlphanumericRunsRegex();

	/// <summary>True when <paramref name="slug"/> satisfies the DB constraint.</summary>
	public static bool IsValid(string? slug)
		=> !string.IsNullOrEmpty(slug) && ValidatorRegex().IsMatch(slug);

	/// <summary>
	/// Derive a slug from a free-form title. Strips diacritics, lowercases,
	/// replaces each run of non-alphanumeric characters with a single
	/// hyphen, trims leading/trailing hyphens, and truncates to
	/// <see cref="MaxLength"/>. Returns <see langword="null"/> when the
	/// input produces an empty slug (e.g. all whitespace or non-Latin
	/// punctuation only) — callers must surface a 400 with a hint to
	/// supply <c>slug</c> explicitly.
	/// </summary>
	public static string? From(string title)
	{
		if (string.IsNullOrWhiteSpace(title))
		{
			return null;
		}

		// Decompose accented letters so "résumé" -> "resume" not "rsum".
		var normalized = title.Normalize(NormalizationForm.FormD);
		var sb = new StringBuilder(normalized.Length);
		foreach (var ch in normalized)
		{
			if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
			{
				sb.Append(ch);
			}
		}
		var stripped = sb.ToString().ToLowerInvariant();

		// Replace every run of non-alphanumeric characters with a single
		// hyphen; trim leading/trailing hyphens; truncate; trim again in
		// case the truncation left a trailing hyphen.
		var hyphenated = NonAlphanumericRunsRegex().Replace(stripped, "-").Trim('-');
		if (hyphenated.Length > MaxLength)
		{
			hyphenated = hyphenated[..MaxLength].TrimEnd('-');
		}

		return hyphenated.Length == 0 ? null : hyphenated;
	}
}
