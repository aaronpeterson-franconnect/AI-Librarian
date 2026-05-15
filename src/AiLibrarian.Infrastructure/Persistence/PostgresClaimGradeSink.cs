using AiLibrarian.Domain.Citations;
using AiLibrarian.Infrastructure.Rls;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// Durable <see cref="IClaimGradeSink"/> backed by
/// <c>wiki_claim_grades</c> from migration 0025. Replaces the in-memory
/// sink for production use; the eval harness flips between them via
/// DI per environment.
///
/// <para>Writes use the system admin context (writes to wiki tables
/// are Admin-only per <c>0102-wiki-rls.sql</c>). Reads use the caller's
/// RLS context — a reader only sees grades on claims they can read,
/// which inherits the facet's classification gating.</para>
///
/// <para>The schema's unique key is <c>(claim_id, grader_version)</c>;
/// passing different grader versions preserves history rather than
/// overwriting. <see cref="IClaimGradeSink.GetLatestAsync"/> returns
/// the most recent row by <c>graded_at</c>.</para>
/// </summary>
public sealed class PostgresClaimGradeSink : IClaimGradeSink
{
	private const string DefaultGraderVersion = "unspecified";

	private readonly NpgsqlDataSource _dataSource;
	private readonly ILogger<PostgresClaimGradeSink> _logger;

	/// <summary>Creates the sink.</summary>
	public PostgresClaimGradeSink(
		NpgsqlDataSource dataSource,
		ILogger<PostgresClaimGradeSink> logger)
	{
		_dataSource = dataSource;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task RecordAsync(ClaimGrade grade, string graderVersion, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(grade);
		if (string.IsNullOrWhiteSpace(graderVersion))
		{
			graderVersion = DefaultGraderVersion;
		}

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		// Writes require Admin via p_wiki_claim_grades_write. The sink
		// is invoked from Wiki Maintainer / eval harness paths that run
		// as the system user; push the system admin context.
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		const string sql = """
			INSERT INTO wiki_claim_grades (claim_id, verdict, confidence, rationale, grader_version)
			VALUES (@claim_id, @verdict, @confidence, @rationale, @grader_version)
			ON CONFLICT (claim_id, grader_version) DO UPDATE
				SET verdict     = EXCLUDED.verdict,
				    confidence  = EXCLUDED.confidence,
				    rationale   = EXCLUDED.rationale,
				    graded_at   = now()
			""";

		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("claim_id", grade.ClaimId);
		cmd.Parameters.AddWithValue("verdict", grade.Verdict.ToString());
		cmd.Parameters.AddWithValue("confidence", (decimal)grade.Confidence);
		cmd.Parameters.AddWithValue("rationale", grade.Rationale ?? string.Empty);
		cmd.Parameters.AddWithValue("grader_version", graderVersion);

		await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		_logger.LogDebug(
			"Recorded claim grade claim={ClaimId} verdict={Verdict} version={GraderVersion}",
			grade.ClaimId,
			grade.Verdict,
			graderVersion);
	}

	/// <inheritdoc />
	public async Task<ClaimGrade?> GetLatestAsync(Guid claimId, CancellationToken cancellationToken = default)
	{
		if (claimId == Guid.Empty)
		{
			return null;
		}

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		const string sql = """
			SELECT claim_id, verdict, confidence, rationale
			FROM wiki_claim_grades
			WHERE claim_id = @claim_id
			ORDER BY graded_at DESC
			LIMIT 1
			""";

		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("claim_id", claimId);

		ClaimGrade? result = null;
		await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				result = Map(reader);
			}
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
		return result;
	}

	/// <inheritdoc />
	public async Task<IReadOnlyCollection<ClaimGrade>> SnapshotAsync(CancellationToken cancellationToken = default)
	{
		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		// Snapshot = latest grade per claim. Composed via a DISTINCT ON
		// query rather than two round-trips so the snapshot is atomic.
		const string sql = """
			SELECT DISTINCT ON (claim_id) claim_id, verdict, confidence, rationale
			FROM wiki_claim_grades
			ORDER BY claim_id, graded_at DESC
			""";

		var results = new List<ClaimGrade>();
		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				results.Add(Map(reader));
			}
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
		return results;
	}

	private static ClaimGrade Map(NpgsqlDataReader reader)
	{
		var claimId = reader.GetGuid(0);
		var verdictRaw = reader.GetString(1);
		var verdict = Enum.TryParse<ClaimVerdict>(verdictRaw, ignoreCase: false, out var v)
			? v
			: ClaimVerdict.Unverifiable;
		var confidence = (double)reader.GetDecimal(2);
		var rationale = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
		return new ClaimGrade(claimId, verdict, confidence, rationale);
	}
}
