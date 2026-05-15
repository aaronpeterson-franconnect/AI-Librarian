using System.Text.Json;

using AiLibrarian.IngestWorker;
using AiLibrarian.Skills.Markdown;

namespace AiLibrarian.IngestWorker.Tests;

public sealed class IngestJobMessageReaderTests
{
	[Fact]
	public void TryRead_valid_camelCase_deserializes()
	{
		var json = """
			{
			  "blobUri": "https://acct.blob.core.windows.net/x/y.md",
			  "correlationId": "run-1",
			  "contentType": "text/markdown",
			  "originalFileName": "y.md"
			}
			""";
		var ok = IngestJobMessageReader.TryRead(BinaryData.FromString(json), out var msg, out var err);
		ok.Should().BeTrue(err);
		msg!.BlobUri.Should().Contain("blob.core.windows.net");
		msg.CorrelationId.Should().Be("run-1");
		msg.ContentType.Should().Be("text/markdown");
	}

	[Fact]
	public void TryRead_empty_body_fails()
	{
		IngestJobMessageReader.TryRead(BinaryData.Empty, out _, out var err).Should().BeFalse();
		err.Should().ContainEquivalentOf("empty");
	}

	[Fact]
	public void TryRead_invalid_json_fails()
	{
		IngestJobMessageReader.TryRead(BinaryData.FromString("{"), out _, out _).Should().BeFalse();
	}

	[Fact]
	public void TryRead_missing_blobUri_fails_validation()
	{
		var json = JsonSerializer.Serialize(new { contentType = "text/plain" });
		IngestJobMessageReader.TryRead(BinaryData.FromString(json), out _, out var err).Should().BeFalse();
		err.Should().ContainEquivalentOf("bloburi");
	}
}
