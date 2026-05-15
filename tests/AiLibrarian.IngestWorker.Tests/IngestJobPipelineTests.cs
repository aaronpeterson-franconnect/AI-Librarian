using System.Text;

using AiLibrarian.Auditing;
using AiLibrarian.Domain.Ingest;
using AiLibrarian.Domain.Skills;
using AiLibrarian.Domain.Sources;
using AiLibrarian.Infrastructure.Persistence;
using AiLibrarian.Infrastructure.Rls;
using AiLibrarian.IngestWorker;
using AiLibrarian.LlmGateway.Abstractions;
using AiLibrarian.Skills.Markdown;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiLibrarian.IngestWorker.Tests;

public sealed class IngestJobPipelineTests
{
	private static SkillRegistry Skills() => new([new MarkdownSkill()]);

	[Fact]
	public async Task RunContentPipeline_false_does_not_open_blob()
	{
		var opener = new RecordingBlobOpener();
		var capture = new CaptureChunkPersistence();
		var opts = Options.Create(new IngestWorkerOptions
		{
			Processing = new IngestProcessingOptions { RunContentPipeline = false },
		});
		var pipeline = new IngestJobPipeline(
			NullLogger<IngestJobPipeline>.Instance,
			Skills(),
			opener,
			capture,
			new RecordingSourceWriter(),
			new ChunkEmbeddingService(new StubEmbeddingProvider(), capture, opts, NullLogger<ChunkEmbeddingService>.Instance),
			new NoOpAuditWriter(NullLogger<NoOpAuditWriter>.Instance),
			opts);

		var job = new IngestJobMessage
		{
			BlobUri = "https://example.blob.core.windows.net/c/a.md",
			ContentType = "text/markdown",
		};
		job.TryValidate(out _).Should().BeTrue();

		var result = await pipeline.RunAsync(job, CancellationToken.None);
		result.Ok.Should().BeTrue();
		opener.OpenCount.Should().Be(0);
		capture.LastChunks.Should().BeNull();
	}

	[Fact]
	public async Task RunContentPipeline_true_writes_chunks_when_db_and_source_configured()
	{
		var dept = Guid.NewGuid();
		var uid = Guid.NewGuid();
		var sourceId = Guid.NewGuid();
		var opener = new RecordingBlobOpener
		{
			Bytes = Encoding.UTF8.GetBytes("# Title\n\nParagraph one."),
		};
		var capture = new CaptureChunkPersistence();
		var opts = Options.Create(new IngestWorkerOptions
		{
			Processing = new IngestProcessingOptions { RunContentPipeline = true },
			Database = new IngestWorkerDatabaseOptions
			{
				ConnectionString = "Host=unused",
				Rls = new IngestRlsOptions
				{
					UserId = uid,
					ContributorDepartmentIds = [dept],
				},
			},
		});
		var pipeline = new IngestJobPipeline(
			NullLogger<IngestJobPipeline>.Instance,
			Skills(),
			opener,
			capture,
			new RecordingSourceWriter(),
			new ChunkEmbeddingService(new StubEmbeddingProvider(), capture, opts, NullLogger<ChunkEmbeddingService>.Instance),
			new NoOpAuditWriter(NullLogger<NoOpAuditWriter>.Instance),
			opts);

		var job = new IngestJobMessage
		{
			BlobUri = "https://example.blob.core.windows.net/c/a.md",
			ContentType = "text/markdown",
			SourceId = sourceId,
		};
		job.TryValidate(out _).Should().BeTrue();

		var result = await pipeline.RunAsync(job, CancellationToken.None);
		result.Ok.Should().BeTrue();
		opener.OpenCount.Should().Be(1);
		capture.LastSourceId.Should().Be(sourceId);
		capture.LastChunks.Should().NotBeNull();
		capture.LastChunks!.Count.Should().BeGreaterThan(0);
		capture.LastRls.Should().NotBeNull();
		capture.LastRls!.ContributorDepartmentIds.Should().Contain(dept);
		capture.UpdateEmbeddingsCallCount.Should().Be(0);
	}

	[Fact]
	public async Task RunContentPipeline_true_without_skill_returns_dead_letter()
	{
		var opener = new RecordingBlobOpener();
		var opts = Options.Create(new IngestWorkerOptions { Processing = new IngestProcessingOptions { RunContentPipeline = true } });
		var pipeline = new IngestJobPipeline(
			NullLogger<IngestJobPipeline>.Instance,
			Skills(),
			opener,
			new NullSourceChunkPersistence(),
			new RecordingSourceWriter(),
			new ChunkEmbeddingService(new StubEmbeddingProvider(), new NullSourceChunkPersistence(), opts, NullLogger<ChunkEmbeddingService>.Instance),
			new NoOpAuditWriter(NullLogger<NoOpAuditWriter>.Instance),
			opts);

		var job = new IngestJobMessage
		{
			BlobUri = "https://example.blob.core.windows.net/c/a.bin",
			ContentType = "application/octet-stream",
		};
		job.TryValidate(out _).Should().BeTrue();

		var result = await pipeline.RunAsync(job, CancellationToken.None);
		result.Ok.Should().BeFalse();
		result.DeadLetterReason.Should().Be("NoSkill");
		opener.OpenCount.Should().Be(0);
	}

	[Fact]
	public async Task RunContentPipeline_embeddings_calls_update_when_enabled()
	{
		var capture = new CaptureChunkPersistence();
		var opts = Options.Create(new IngestWorkerOptions
		{
			Processing = new IngestProcessingOptions { RunContentPipeline = true, GenerateEmbeddings = true },
			Database = new IngestWorkerDatabaseOptions
			{
				ConnectionString = "Host=unused",
				Rls = new IngestRlsOptions
				{
					UserId = Guid.NewGuid(),
					ContributorDepartmentIds = [Guid.NewGuid()],
				},
			},
			Embeddings = new IngestEmbeddingsOptions
			{
				ModelDeploymentName = "emb-deploy",
				ExpectedDimensions = 1536,
			},
		});
		var pipeline = new IngestJobPipeline(
			NullLogger<IngestJobPipeline>.Instance,
			Skills(),
			new RecordingBlobOpener { Bytes = Encoding.UTF8.GetBytes("# A\n\nB.") },
			capture,
			new RecordingSourceWriter(),
			new ChunkEmbeddingService(new StubEmbeddingProvider(), capture, opts, NullLogger<ChunkEmbeddingService>.Instance),
			new NoOpAuditWriter(NullLogger<NoOpAuditWriter>.Instance),
			opts);

		var sourceId = Guid.NewGuid();
		var job = new IngestJobMessage
		{
			BlobUri = "https://example.blob.core.windows.net/c/a.md",
			ContentType = "text/markdown",
			SourceId = sourceId,
		};
		job.TryValidate(out _).Should().BeTrue();

		var result = await pipeline.RunAsync(job, CancellationToken.None);
		result.Ok.Should().BeTrue();
		capture.UpdateEmbeddingsCallCount.Should().Be(1);
		capture.LastEmbeddingModel.Should().Be("emb-deploy");
		capture.LastEmbeddingVectors.Should().NotBeNull();
		capture.LastEmbeddingVectors!.Count.Should().Be(capture.LastChunks!.Count);
	}

	private sealed class StubEmbeddingProvider : IEmbeddingProvider
	{
		public string ProviderId => "stub";

		public Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(
			string model,
			IReadOnlyList<string> inputs,
			Guid correlationId,
			CancellationToken cancellationToken)
		{
			var vec = new float[1536];
			vec[0] = 1f;
			var mem = new ReadOnlyMemory<float>(vec);
			var list = inputs.Select(_ => mem).ToList();
			return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(list);
		}
	}

	private sealed class RecordingBlobOpener : IBlobContentOpener
	{
		public byte[] Bytes { get; init; } = Encoding.UTF8.GetBytes("# x");

		public int OpenCount { get; private set; }

		public Task<Stream> OpenReadAsync(Uri blobUri, CancellationToken cancellationToken)
		{
			OpenCount++;
			return Task.FromResult<Stream>(new MemoryStream(Bytes));
		}
	}

	private sealed class RecordingSourceWriter : ISourceWriter
	{
		public Guid? LastChecksumSourceId { get; private set; }

		public string? LastChecksumSha256 { get; private set; }

		public long? LastSizeBytes { get; private set; }

		public bool ReturnUpdated { get; init; } = true;

		public Task<Guid> CreateAsync(
			RlsSessionContext context,
			SourceSubmission submission,
			CancellationToken cancellationToken = default)
			=> Task.FromResult(Guid.NewGuid());

		public Task<bool> UpdateChecksumAndSizeAsync(
			RlsSessionContext context,
			Guid sourceId,
			string checksumSha256,
			long sizeBytes,
			CancellationToken cancellationToken = default)
		{
			LastChecksumSourceId = sourceId;
			LastChecksumSha256 = checksumSha256;
			LastSizeBytes = sizeBytes;
			_ = context;
			return Task.FromResult(ReturnUpdated);
		}
	}

	private sealed class CaptureChunkPersistence : ISourceChunkPersistence
	{
		public Guid? LastSourceId { get; private set; }

		public IReadOnlyList<Chunk>? LastChunks { get; private set; }

		public RlsSessionContext? LastRls { get; private set; }

		public int UpdateEmbeddingsCallCount { get; private set; }

		public string? LastEmbeddingModel { get; private set; }

		public IReadOnlyList<(int OrderIndex, ReadOnlyMemory<float> Embedding)>? LastEmbeddingVectors { get; private set; }

		public Task ReplaceChunksForSourceAsync(
			RlsSessionContext sessionContext,
			Guid sourceId,
			IReadOnlyList<Chunk> chunks,
			CancellationToken cancellationToken)
		{
			LastRls = sessionContext;
			LastSourceId = sourceId;
			LastChunks = chunks;
			return Task.CompletedTask;
		}

		public Task UpdateEmbeddingsForSourceAsync(
			RlsSessionContext sessionContext,
			Guid sourceId,
			string embeddingModel,
			IReadOnlyList<(int OrderIndex, ReadOnlyMemory<float> Embedding)> vectors,
			CancellationToken cancellationToken)
		{
			UpdateEmbeddingsCallCount++;
			LastEmbeddingModel = embeddingModel;
			LastEmbeddingVectors = vectors;
			_ = sessionContext;
			_ = sourceId;
			return Task.CompletedTask;
		}
	}
}
