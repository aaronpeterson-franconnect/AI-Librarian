using AiLibrarian.Domain.Ingest;

namespace AiLibrarian.Domain.Tests;

public sealed class IngestJobMessageTests
{
	[Fact]
	public void TryValidate_empty_blob_fails()
	{
		var m = new IngestJobMessage { BlobUri = "" };
		m.TryValidate(out var err).Should().BeFalse();
		err.Should().Contain("BlobUri");
	}

	[Fact]
	public void TryValidate_relative_uri_fails()
	{
		var m = new IngestJobMessage { BlobUri = "/container/blob" };
		m.TryValidate(out var err).Should().BeFalse();
		err.Should().NotBeNullOrEmpty();
	}

	[Fact]
	public void TryValidate_https_blob_succeeds()
	{
		var m = new IngestJobMessage
		{
			BlobUri = "https://example.blob.core.windows.net/c/file.md",
			CorrelationId = "c1",
			ContentType = "text/markdown",
		};
		m.TryValidate(out _).Should().BeTrue();
	}
}
