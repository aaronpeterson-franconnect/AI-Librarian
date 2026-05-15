using AiLibrarian.Domain;
using AiLibrarian.Infrastructure.Rls;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// Read-side surface for the cascade-regeneration worker. Returns the
/// set of facets that have at least one dangling citation -- the worker
/// regenerates each, bounded per tick.
///
/// <para>Extracted as an interface so the
/// <c>WikiMaintenanceHostedService</c> and the maintain/discover admin
/// endpoint handlers can be unit-tested without a real
/// <c>NpgsqlDataSource</c>. The single production implementation is
/// <see cref="DanglingFacetReader"/>.</para>
/// </summary>
public interface IDanglingFacetReader
{
	/// <summary>
	/// Find every facet that has at least one dangling citation. Runs
	/// in the system admin context (writes / scheduled work do).
	/// </summary>
	/// <param name="since">Lower-bound on citation creation time; null for "all time."</param>
	/// <param name="departmentId">Restrict to a department; null for all departments the system can see.</param>
	/// <param name="maxFacets">Cap on returned facets.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	Task<IReadOnlyList<DanglingFacet>> FindAsync(
		DateTimeOffset? since,
		Guid? departmentId,
		int maxFacets,
		CancellationToken cancellationToken);
}

/// <summary>
/// Calls the <c>audit_dangling_citations</c> SQL function and groups
/// the resulting rows by the affected facet. The cascade-regeneration
/// worker uses this to find facets whose source material went
/// dangling and queue them for regeneration.
///
/// <para>One pass returns the distinct
/// (page_id, page_slug, page_title, page_department_id, classification, persona_id)
/// tuples that need a fresh revision, sorted by the count of dangling
/// citations descending — so the worker tackles the worst cases first
/// when its per-tick budget is bounded.</para>
/// </summary>
public sealed class DanglingFacetReader : IDanglingFacetReader
{
	private readonly NpgsqlDataSource _dataSource;
	private readonly ILogger<DanglingFacetReader> _logger;

	/// <summary>Creates the reader.</summary>
	public DanglingFacetReader(NpgsqlDataSource dataSource, ILogger<DanglingFacetReader> logger)
	{
		_dataSource = dataSource;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<DanglingFacet>> FindAsync(
		DateTimeOffset? since,
		Guid? departmentId,
		int maxFacets,
		CancellationToken cancellationToken)
	{
		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		// audit_dangling_citations returns one row per dangling
		// citation. Group up to the facet level (the regeneration unit)
		// + carry the page metadata so the worker has enough to call
		// IWikiMaintainer without a second round trip.
		const string sql = """
			SELECT
				wp.id                                          AS page_id,
				wp.slug                                        AS page_slug,
				wp.title                                       AS page_title,
				wp.department_id                               AS department_id,
				wpr.min_classification                         AS classification,
				wpr.persona_id                                 AS persona_id,
				COUNT(*)                                       AS dangling_count
			FROM audit_dangling_citations(@dept, @since) d
			INNER JOIN wiki_claims wc        ON wc.id = d.claim_id
			INNER JOIN wiki_page_revisions wpr ON wpr.id = wc.revision_id
			INNER JOIN wiki_pages wp         ON wp.id = wpr.page_id
			GROUP BY wp.id, wp.slug, wp.title, wp.department_id, wpr.min_classification, wpr.persona_id
			ORDER BY COUNT(*) DESC
			LIMIT @max
			""";

		var results = new List<DanglingFacet>();
		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("dept", (object?)departmentId ?? DBNull.Value);
		cmd.Parameters.AddWithValue("since", (object?)since ?? DBNull.Value);
		cmd.Parameters.AddWithValue("max", maxFacets);

		await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				var classificationStr = reader.GetString(4);
				if (!Enum.TryParse<Classification>(classificationStr, ignoreCase: true, out var classification))
				{
					classification = Classification.Internal;
				}

				results.Add(new DanglingFacet(
					PageId: reader.GetGuid(0),
					PageSlug: reader.GetString(1),
					PageTitle: reader.GetString(2),
					DepartmentId: reader.GetGuid(3),
					Classification: classification,
					PersonaId: reader.IsDBNull(5) ? null : reader.GetGuid(5),
					DanglingCount: reader.GetInt64(6)));
			}
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		if (results.Count > 0)
		{
			_logger.LogInformation(
				"DanglingFacetReader: {Count} facet(s) with dangling citations (dept={Dept} since={Since})",
				results.Count,
				departmentId,
				since);
		}

		return results;
	}
}

/// <summary>One facet that needs regeneration because its source material went dangling.</summary>
public sealed record DanglingFacet(
	Guid PageId,
	string PageSlug,
	string PageTitle,
	Guid DepartmentId,
	Classification Classification,
	Guid? PersonaId,
	long DanglingCount);
