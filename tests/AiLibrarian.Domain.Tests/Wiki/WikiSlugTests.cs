using AiLibrarian.Domain.Wiki;

namespace AiLibrarian.Domain.Tests.Wiki;

/// <summary>
/// Slug-helper unit tests. The DB check constraint is the source of
/// truth (<c>^[a-z0-9][a-z0-9\-]{0,254}$</c> in Liquibase 0020); these
/// tests pin the C# helper to produce constraint-compliant slugs and
/// to validate operator-supplied slugs the same way.
/// </summary>
public sealed class WikiSlugTests
{
	[Theory]
	[InlineData("ingest-worker")]
	[InlineData("a")]
	[InlineData("0")]
	[InlineData("page-1")]
	[InlineData("a-very-long-but-still-valid-slug-name-with-some-hyphens")]
	public void IsValid_returns_true_for_constraint_compliant_slugs(string slug)
	{
		WikiSlug.IsValid(slug).Should().BeTrue();
	}

	[Theory]
	[InlineData("")]
	[InlineData(" ")]
	[InlineData("-leading-hyphen")]
	[InlineData("UPPER")]
	[InlineData("with space")]
	[InlineData("with_underscore")]
	[InlineData("ümlaut")]
	public void IsValid_returns_false_for_constraint_violators(string slug)
	{
		WikiSlug.IsValid(slug).Should().BeFalse();
	}

	[Fact]
	public void IsValid_returns_false_for_slug_exceeding_max_length()
	{
		var tooLong = new string('a', WikiSlug.MaxLength + 1);
		WikiSlug.IsValid(tooLong).Should().BeFalse();
	}

	[Theory]
	[InlineData("Ingest Worker", "ingest-worker")]
	[InlineData("Ingest    Worker", "ingest-worker")]
	[InlineData("Ingest_Worker", "ingest-worker")]
	[InlineData("How does the worker boot?", "how-does-the-worker-boot")]
	[InlineData("résumé", "resume")]
	[InlineData("API: rotation policy", "api-rotation-policy")]
	[InlineData("--leading--hyphens--", "leading-hyphens")]
	[InlineData("trailing!!!", "trailing")]
	public void From_derives_valid_slug(string title, string expected)
	{
		var slug = WikiSlug.From(title);
		slug.Should().Be(expected);
		WikiSlug.IsValid(slug).Should().BeTrue();
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("!!!")]
	[InlineData("•••")]
	public void From_returns_null_when_input_yields_empty_slug(string? title)
	{
		WikiSlug.From(title!).Should().BeNull();
	}

	[Fact]
	public void From_truncates_oversize_input_and_trims_trailing_hyphen()
	{
		// 300 chars of "ab " produces "ab-ab-ab-..." which would otherwise
		// be ~300 chars; we cap at MaxLength and strip the trailing hyphen
		// that the truncation point may land on.
		var title = string.Concat(Enumerable.Repeat("ab ", 300));
		var slug = WikiSlug.From(title);
		slug.Should().NotBeNull();
		slug!.Length.Should().BeLessThanOrEqualTo(WikiSlug.MaxLength);
		slug.Should().NotEndWith("-");
		WikiSlug.IsValid(slug).Should().BeTrue();
	}
}
