using AiLibrarian.Domain;
using AiLibrarian.Infrastructure.Rls;

using Npgsql;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// Contract for "next revision number on this facet" used by the
/// maintain + discover admin endpoints and the cascade-regeneration
/// hosted service. Extracted as an interface so handler-level tests
/// can stub it; production wiring is <see cref="WikiRevisionNumberer"/>.
/// </summary>
public interface IWikiRevisionNumberer
{
	/// <summary>Return the next <c>revision_number</c> to use for this facet (max + 1, or 1 if no revisions exist).</summary>
	Task<int> NextAsync(
		Guid pageId,
		Classification classification,
		Guid? personaId,
		CancellationToken cancellationToken);
}

/// <summary>
/// Computes the next monotonic <c>revision_number</c> for a facet.
/// The schema's unique index on
/// (page_id, min_classification, COALESCE(persona_id, sentinel), revision_number)
/// is the source of truth; this helper just reads the max and adds
/// one. Inside the Maintainer's commit transaction the writer would
/// surface a unique-index 23505 if a concurrent commit beat us to
/// the punch — but for Phase 2 first cut single-threaded operation
/// is the realistic case.
/// </summary>
public sealed class WikiRevisionNumberer : IWikiRevisionNumberer
{
	private readonly NpgsqlDataSource _dataSource;

	/// <summary>Creates the numberer.</summary>
	public WikiRevisionNumberer(NpgsqlDataSource dataSource)
	{
		_dataSource = dataSource;
	}

	/// <inheritdoc />
	public async Task<int> NextAsync(
		Guid pageId,
		Classification classification,
		Guid? personaId,
		CancellationToken cancellationToken)
	{
		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		const string sql = """
			SELECT COALESCE(MAX(revision_number), 0) + 1
			FROM wiki_page_revisions
			WHERE page_id = @page_id
			  AND min_classification = @classification
			  AND COALESCE(persona_id, '00000000-0000-0000-0000-000000000000'::uuid)
			    = COALESCE(@persona_id, '00000000-0000-0000-0000-000000000000'::uuid)
			""";

		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("page_id", pageId);
		cmd.Parameters.AddWithValue("classification", classification.ToString());
		cmd.Parameters.AddWithValue("persona_id", (object?)personaId ?? DBNull.Value);

		var next = (int)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
		return next;
	}
}
