using System.Diagnostics;
using System.Security.Cryptography;

using AiLibrarian.Auditing;
using AiLibrarian.Domain.Ingest;
using AiLibrarian.Domain.Skills;

using AiLibrarian.Infrastructure.Persistence;
using AiLibrarian.Infrastructure.Rls;

using Microsoft.Extensions.Options;

namespace AiLibrarian.IngestWorker;

/// <summary>Orchestrates blob download, skill canonicalization, and optional chunk persistence.</summary>
internal sealed class IngestJobPipeline
{
	private readonly ILogger<IngestJobPipeline> _logger;
	private readonly ISkillRegistry _skillRegistry;
	private readonly IBlobContentOpener _blobContentOpener;
	private readonly ISourceChunkPersistence _chunkPersistence;
	private readonly ISourceWriter _sourceWriter;
	private readonly ChunkEmbeddingService _chunkEmbedding;
	private readonly IAuditWriter _auditWriter;
	private readonly IOptions<IngestWorkerOptions> _options;

	public IngestJobPipeline(
		ILogger<IngestJobPipeline> logger,
		ISkillRegistry skillRegistry,
		IBlobContentOpener blobContentOpener,
		ISourceChunkPersistence chunkPersistence,
		ISourceWriter sourceWriter,
		ChunkEmbeddingService chunkEmbedding,
		IAuditWriter auditWriter,
		IOptions<IngestWorkerOptions> options)
	{
		_logger = logger;
		_skillRegistry = skillRegistry;
		_blobContentOpener = blobContentOpener;
		_chunkPersistence = chunkPersistence;
		_sourceWriter = sourceWriter;
		_chunkEmbedding = chunkEmbedding;
		_auditWriter = auditWriter;
		_options = options;
	}

	public async Task<IngestPipelineResult> RunAsync(IngestJobMessage job, CancellationToken cancellationToken)
	{
		var opts = _options.Value;
		var skill = IngestSkillResolver.Resolve(_skillRegistry, job);

		using var pipelineActivity = AiLibActivitySource.Ingest.StartActivity("ailib.ingest.pipeline", ActivityKind.Internal);
		pipelineActivity?.SetTag(AiLibActivitySource.Attributes.SourceId, job.SourceId);
		pipelineActivity?.SetTag(AiLibActivitySource.Attributes.SkillName, skill?.Name ?? "(none)");
		if (Guid.TryParse(job.CorrelationId, out var corr))
		{
			pipelineActivity?.SetTag(AiLibActivitySource.Attributes.CorrelationId, corr);
		}

		if (!opts.Processing.RunContentPipeline)
		{
			_logger.LogInformation(
				"Ingest job correlation={Correlation} blobUri={BlobUri} skill={Skill} sourceId={SourceId} (content pipeline disabled).",
				job.CorrelationId,
				job.BlobUri,
				skill?.Name ?? "(none)",
				job.SourceId);
			return IngestPipelineResult.Completed();
		}

		if (skill is null)
		{
			return IngestPipelineResult.DeadLetter(
				"NoSkill",
				"No skill could be resolved from ContentType / OriginalFileName hints.");
		}

		var blobUri = new Uri(job.BlobUri, UriKind.Absolute);
		Stream rawStream;
		try
		{
			rawStream = await _blobContentOpener.OpenReadAsync(blobUri, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to open blob {BlobUri}", job.BlobUri);
			await TryAuditFailureAsync(job, "BlobOpenFailed", ex, cancellationToken).ConfigureAwait(false);
			return IngestPipelineResult.DeadLetter("BlobOpenFailed", ex.Message);
		}

		// Buffer the blob so we can compute SHA-256 + size and then re-read
		// for the skill. Phase 1 cap is the API's MultipartBodyLengthLimit
		// (100 MB); larger formats land via streamed-hash refactor in
		// Phase 1+. The buffer is disposed on every exit path.
		var buffered = new MemoryStream();
		try
		{
			await using (rawStream)
			{
				await rawStream.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
			}

			buffered.Position = 0;
			var sizeBytes = buffered.Length;
			string sha256Hex;
			using (var sha = SHA256.Create())
			{
				var hash = await sha.ComputeHashAsync(buffered, cancellationToken).ConfigureAwait(false);
				sha256Hex = Convert.ToHexStringLower(hash);
			}

			buffered.Position = 0;

			var mime = string.IsNullOrWhiteSpace(job.ContentType)
				? "application/octet-stream"
				: job.ContentType.Trim();
			var meta = new SourceMetadata(mime, job.OriginalFileName, ContentLength: sizeBytes);

			SkillResult result;
			using (var skillActivity = AiLibActivitySource.Ingest.StartActivity(
				$"ailib.ingest.skill.{skill.Name}",
				ActivityKind.Internal))
			{
				skillActivity?.SetTag(AiLibActivitySource.Attributes.SkillName, skill.Name);
				skillActivity?.SetTag(AiLibActivitySource.Attributes.ChecksumSha256, sha256Hex);
				skillActivity?.SetTag("ailib.ingest.size_bytes", sizeBytes);

				try
				{
					result = await skill.CanonicalizeAsync(buffered, meta, cancellationToken).ConfigureAwait(false);
					skillActivity?.SetTag(AiLibActivitySource.Attributes.ChunkCount, result.Chunks.Count);
				}
				catch (Exception ex)
				{
					skillActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
					_logger.LogWarning(ex, "Skill {Skill} failed for blob {BlobUri}", skill.Name, job.BlobUri);
					await TryAuditFailureAsync(job, "CanonicalizeFailed", ex, cancellationToken).ConfigureAwait(false);
					return IngestPipelineResult.DeadLetter("CanonicalizeFailed", ex.Message);
				}
			}

			var dbConn = opts.Database.ConnectionString;
			RlsSessionContext? rlsForDb = null;

			if (!string.IsNullOrWhiteSpace(dbConn) && job.SourceId is { } sourceIdForChunks)
			{
				rlsForDb = opts.Database.Rls.ToRlsSessionContext();

				// Stamp the source row with checksum + size before chunks land
				// so retrieval sees them together. Failures are dead-letter:
				// the row's existence is a precondition for chunks.
				try
				{
					var updated = await _sourceWriter
						.UpdateChecksumAndSizeAsync(
							rlsForDb,
							sourceIdForChunks,
							sha256Hex,
							sizeBytes,
							cancellationToken)
						.ConfigureAwait(false);

					if (!updated)
					{
						_logger.LogWarning(
							"Source row not found for ingest source={SourceId}; dead-letter.",
							sourceIdForChunks);
						await TryAuditFailureAsync(
								job,
								"SourceMissing",
								new InvalidOperationException("UpdateChecksumAndSize touched 0 rows."),
								cancellationToken)
							.ConfigureAwait(false);
						return IngestPipelineResult.DeadLetter(
							"SourceMissing",
							"Source row not found or hidden by RLS.");
					}
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Source checksum/size update failed source={SourceId}", sourceIdForChunks);
					await TryAuditFailureAsync(job, "SourceUpdateFailed", ex, cancellationToken).ConfigureAwait(false);
					return IngestPipelineResult.DeadLetter("SourceUpdateFailed", ex.Message);
				}

				try
				{
					await _chunkPersistence.ReplaceChunksForSourceAsync(
							rlsForDb,
							sourceIdForChunks,
							result.Chunks,
							cancellationToken)
						.ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Chunk persistence failed for source={SourceId}", sourceIdForChunks);
					await TryAuditFailureAsync(job, "ChunkPersistFailed", ex, cancellationToken).ConfigureAwait(false);
					return IngestPipelineResult.DeadLetter("ChunkPersistFailed", ex.Message);
				}

				await TryAuditCanonicalizedAsync(
						job,
						sourceIdForChunks,
						skill.Name,
						sha256Hex,
						sizeBytes,
						result.Chunks.Count,
						result.Issues.Count,
						cancellationToken)
					.ConfigureAwait(false);
			}
			else if (job.SourceId is not null && string.IsNullOrWhiteSpace(dbConn))
			{
				_logger.LogWarning(
					"Ingest job has SourceId={SourceId} but IngestWorker:Database:ConnectionString is empty; chunks not saved.",
					job.SourceId);
			}

			if (opts.Processing.GenerateEmbeddings && rlsForDb is not null)
			{
				try
				{
					await _chunkEmbedding.TryEmbedAsync(job, rlsForDb, result, cancellationToken).ConfigureAwait(false);

					if (job.SourceId is { } sourceIdForEmbeddings)
					{
						await TryAuditEmbeddedAsync(
								job,
								sourceIdForEmbeddings,
								opts.Embeddings.ModelDeploymentName,
								result.Chunks.Count,
								cancellationToken)
							.ConfigureAwait(false);
					}
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Embedding failed for job correlation={Correlation}", job.CorrelationId);
					await TryAuditFailureAsync(job, "EmbeddingFailed", ex, cancellationToken).ConfigureAwait(false);
					return IngestPipelineResult.DeadLetter("EmbeddingFailed", ex.Message);
				}
			}

			_logger.LogInformation(
				"Ingest completed correlation={Correlation} blobUri={BlobUri} skill={Skill} chunks={ChunkCount} issues={IssueCount}",
				job.CorrelationId,
				job.BlobUri,
				skill.Name,
				result.Chunks.Count,
				result.Issues.Count);

			return IngestPipelineResult.Completed();
		}
		finally
		{
			await buffered.DisposeAsync().ConfigureAwait(false);
		}
	}

	private async Task TryAuditCanonicalizedAsync(
		IngestJobMessage job,
		Guid sourceId,
		string skillName,
		string sha256Hex,
		long sizeBytes,
		int chunkCount,
		int issueCount,
		CancellationToken cancellationToken)
	{
		try
		{
			await _auditWriter.WriteAsync(
				new AuditEvent(
					Id: Guid.NewGuid(),
					OccurredAt: DateTimeOffset.UtcNow,
					ActorUserId: AuditConstants.SystemUserId,
					ActorRole: null,
					OriginatedBy: null,
					DepartmentId: null,
					EventType: "ingest",
					EventSubtype: "canonicalized",
					TargetKind: "source",
					TargetId: sourceId,
					CorrelationId: ParseCorrelationGuid(job.CorrelationId),
					Outcome: EventOutcome.Success,
					ErrorClass: null,
					Llm: null,
					Details: new Dictionary<string, object?>
					{
						["skill"] = skillName,
						["sha256"] = sha256Hex,
						["size_bytes"] = sizeBytes,
						["chunks"] = chunkCount,
						["issues"] = issueCount,
					}),
				AuditCriticality.BestEffort,
				cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Best-effort canonicalized audit dropped");
		}
	}

	private async Task TryAuditEmbeddedAsync(
		IngestJobMessage job,
		Guid sourceId,
		string model,
		int vectorCount,
		CancellationToken cancellationToken)
	{
		try
		{
			await _auditWriter.WriteAsync(
				new AuditEvent(
					Id: Guid.NewGuid(),
					OccurredAt: DateTimeOffset.UtcNow,
					ActorUserId: AuditConstants.SystemUserId,
					ActorRole: null,
					OriginatedBy: null,
					DepartmentId: null,
					EventType: "ingest",
					EventSubtype: "embedded",
					TargetKind: "source",
					TargetId: sourceId,
					CorrelationId: ParseCorrelationGuid(job.CorrelationId),
					Outcome: EventOutcome.Success,
					ErrorClass: null,
					Llm: null,
					Details: new Dictionary<string, object?>
					{
						["embedding_model"] = model,
						["vector_count"] = vectorCount,
					}),
				AuditCriticality.BestEffort,
				cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Best-effort embedded audit dropped");
		}
	}

	private async Task TryAuditFailureAsync(
		IngestJobMessage job,
		string reason,
		Exception ex,
		CancellationToken cancellationToken)
	{
		try
		{
			await _auditWriter.WriteAsync(
				new AuditEvent(
					Id: Guid.NewGuid(),
					OccurredAt: DateTimeOffset.UtcNow,
					ActorUserId: AuditConstants.SystemUserId,
					ActorRole: null,
					OriginatedBy: null,
					DepartmentId: null,
					EventType: "ingest",
					EventSubtype: "failed",
					TargetKind: "source",
					TargetId: job.SourceId,
					CorrelationId: ParseCorrelationGuid(job.CorrelationId),
					Outcome: EventOutcome.Failure,
					ErrorClass: reason,
					Llm: null,
					Details: new Dictionary<string, object?>
					{
						["blob_uri"] = job.BlobUri,
						["error_message"] = ex.Message,
					}),
				AuditCriticality.BestEffort,
				cancellationToken).ConfigureAwait(false);
		}
		catch (Exception auditEx)
		{
			_logger.LogDebug(auditEx, "Best-effort failure audit dropped");
		}
	}

	private static Guid ParseCorrelationGuid(string? correlationId)
	{
		if (!string.IsNullOrWhiteSpace(correlationId) && Guid.TryParse(correlationId, out var g))
		{
			return g;
		}

		return Guid.NewGuid();
	}
}
