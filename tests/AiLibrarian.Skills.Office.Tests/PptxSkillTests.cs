using AiLibrarian.Domain.Skills;
using AiLibrarian.Skills.Office;

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

using P = DocumentFormat.OpenXml.Presentation;
using D = DocumentFormat.OpenXml.Drawing;

namespace AiLibrarian.Skills.Office.Tests;

public sealed class PptxSkillTests
{
	[Fact]
	public async Task CanonicalizeAsync_emits_one_chunk_per_slide_with_text()
	{
		var skill = new PptxSkill();
		await using var stream = BuildTwoSlidePptx();

		var result = await skill.CanonicalizeAsync(
			stream,
			new SourceMetadata("application/vnd.openxmlformats-officedocument.presentationml.presentation", "deck.pptx"),
			CancellationToken.None);

		result.Issues.Should().BeEmpty();
		result.Chunks.Should().HaveCount(2);

		result.Chunks[0].ContentMarkdown.Should().Contain("## Slide 1");
		result.Chunks[0].ContentMarkdown.Should().Contain("Welcome");
		result.Chunks[1].ContentMarkdown.Should().Contain("## Slide 2");
		result.Chunks[1].ContentMarkdown.Should().Contain("Architecture review");

		var span = result.Chunks[0].SpanAnchor.AsObject();
		span["type"]!.GetValue<string>().Should().Be("pptx");
		span["slide"]!.GetValue<int>().Should().Be(1);
	}

	private static MemoryStream BuildTwoSlidePptx()
	{
		var ms = new MemoryStream();
		using (var doc = PresentationDocument.Create(ms, PresentationDocumentType.Presentation, autoSave: true))
		{
			var presentationPart = doc.AddPresentationPart();
			presentationPart.Presentation = new P.Presentation(
				new P.SlideIdList(),
				new P.SlideSize { Cx = 9144000, Cy = 6858000 },
				new P.NotesSize { Cx = 6858000, Cy = 9144000 });

			AddSlide(presentationPart, "Welcome", relId: "rIdSlide1", slideId: 256U);
			AddSlide(presentationPart, "Architecture review", relId: "rIdSlide2", slideId: 257U);

			presentationPart.Presentation.Save();
		}

		ms.Position = 0;
		return ms;

		static void AddSlide(PresentationPart presentationPart, string text, string relId, uint slideId)
		{
			var slidePart = presentationPart.AddNewPart<SlidePart>(relId);
			slidePart.Slide = new P.Slide(
				new P.CommonSlideData(
					new P.ShapeTree(
						new P.NonVisualGroupShapeProperties(
							new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
							new P.NonVisualGroupShapeDrawingProperties(),
							new P.ApplicationNonVisualDrawingProperties()),
						new P.GroupShapeProperties(),
						new P.Shape(
							new P.NonVisualShapeProperties(
								new P.NonVisualDrawingProperties { Id = 2U, Name = "Title" },
								new P.NonVisualShapeDrawingProperties(),
								new P.ApplicationNonVisualDrawingProperties()),
							new P.ShapeProperties(),
							new P.TextBody(
								new D.BodyProperties(),
								new D.ListStyle(),
								new D.Paragraph(new D.Run(new D.Text(text))))))),
				new P.ColorMapOverride(new D.MasterColorMapping()));

			var slideIdList = presentationPart.Presentation.SlideIdList!;
			slideIdList.AppendChild(new P.SlideId
			{
				Id = slideId,
				RelationshipId = relId,
			});
		}
	}
}
