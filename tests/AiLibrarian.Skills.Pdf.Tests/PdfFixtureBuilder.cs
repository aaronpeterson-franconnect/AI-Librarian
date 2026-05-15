using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace AiLibrarian.Skills.Pdf.Tests;

/// <summary>
/// Builds tiny PDFs programmatically (Standard 14 fonts → no fixture
/// files checked into the repo). Mirrors the
/// <c>AiLibrarian.Skills.Office.Tests</c> convention.
/// </summary>
internal static class PdfFixtureBuilder
{
	/// <summary>Single-page PDF with one line of text.</summary>
	public static Stream BuildSinglePage(string body = "Hello world. This is page one.")
		=> BuildMultiPage(new[] { body });

	/// <summary>Multi-page PDF, one body string per page.</summary>
	public static Stream BuildMultiPage(IReadOnlyList<string> pageBodies)
	{
		ArgumentNullException.ThrowIfNull(pageBodies);

		var builder = new PdfDocumentBuilder();
		var font = builder.AddStandard14Font(Standard14Font.Helvetica);
		foreach (var body in pageBodies)
		{
			var page = builder.AddPage(595, 842); // A4 portrait, points.
			page.AddText(body, 12, new UglyToad.PdfPig.Core.PdfPoint(50, 800), font);
		}

		return new MemoryStream(builder.Build()) { Position = 0 };
	}

	/// <summary>Empty PDF (one page, no text content) for the no-text issue test.</summary>
	public static Stream BuildEmptyPage()
	{
		var builder = new PdfDocumentBuilder();
		builder.AddPage(595, 842);
		return new MemoryStream(builder.Build()) { Position = 0 };
	}
}
