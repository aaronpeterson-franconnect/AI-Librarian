using AiLibrarian.Domain.Wiki;

using Npgsql;
using NpgsqlTypes;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// Internal helper that performs the wiki_page_revisions +
/// wiki_claims + wiki_claim_citations + page_facets + wiki_pages
/// updates inside an existing connection + transaction. Both
/// <see cref="PostgresWikiRevisionWriter"/> and
/// <see cref="PostgresWikiProposalWriter"/> compose it — the
/// difference is who owns the transaction.
///
/// <para>The caller must have already pushed the system admin RLS
/// context onto the connection.</para>
/// </summary>
internal static class WikiRevisionInserter
{
	/// <summary>
	/// Insert a revision (with claims + citations) and update the
	/// page-facet pointer + page timestamp. Returns the new revision id.
	/// </summary>
	public static async Task<Guid> InsertAsync(
		NpgsqlConnection conn,
		NpgsqlTransaction tx,
		WikiRevisionDraft draft,
		CancellationToken cancellationToken)
	{
		// 1. wiki_page_revisions
		const string revSql = """
			INSERT INTO wiki_page_revisions (page_id, min_classification, persona_id, revision_number, authored_by, body_markdown)
			VALUES (@page_id, @classification, @persona_id, @revno, @authored_by, @body)
			RETURNING id
			""";

		Guid revisionId;
		await using (var cmd = new NpgsqlCommand(revSql, conn, tx))
		{
			cmd.Parameters.AddWithValue("page_id", draft.PageId);
			cmd.Parameters.AddWithValue("classification", draft.MinClassification.ToString());
			cmd.Parameters.AddWithValue("persona_id", (object?)draft.PersonaId ?? DBNull.Value);
			cmd.Parameters.AddWithValue("revno", draft.RevisionNumber);
			cmd.Parameters.AddWithValue("authored_by", draft.AuthoredBy);
			cmd.Parameters.AddWithValue("body", draft.BodyMarkdown);
			revisionId = (Guid)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
		}

		// 2. wiki_claims + wiki_claim_citations
		for (var i = 0; i < draft.Claims.Count; i++)
		{
			var claim = draft.Claims[i];
			Guid claimId;
			const string claimSql = """
				INSERT INTO wiki_claims (revision_id, claim_text, position, facet_classification)
				VALUES (@rev_id, @text, @pos, @classification)
				RETURNING id
				""";

			await using (var claimCmd = new NpgsqlCommand(claimSql, conn, tx))
			{
				claimCmd.Parameters.AddWithValue("rev_id", revisionId);
				claimCmd.Parameters.AddWithValue("text", claim.ClaimText);
				claimCmd.Parameters.AddWithValue("pos", claim.Position);
				claimCmd.Parameters.AddWithValue("classification", draft.MinClassification.ToString());
				claimId = (Guid)(await claimCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
			}

			foreach (var citation in claim.Citations)
			{
				const string citSql = """
					INSERT INTO wiki_claim_citations (claim_id, chunk_id, span_start, span_end, confidence)
					VALUES (@claim_id, @chunk_id, @span_start, @span_end, @confidence)
					""";

				await using var citCmd = new NpgsqlCommand(citSql, conn, tx);
				citCmd.Parameters.AddWithValue("claim_id", claimId);
				citCmd.Parameters.AddWithValue("chunk_id", citation.ChunkId);
				citCmd.Parameters.AddWithValue("span_start", citation.SpanStart);
				citCmd.Parameters.AddWithValue("span_end", citation.SpanEnd);
				citCmd.Parameters.Add(new NpgsqlParameter("confidence", NpgsqlDbType.Numeric)
				{
					Value = (decimal)citation.Confidence,
				});
				await citCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			}
		}

		// 3. page_facets.current_revision_id pointer update.
		const string facetUpdate = """
			UPDATE page_facets
			SET current_revision_id = @rev_id,
			    body_markdown = @body,
			    updated_at = now()
			WHERE page_id = @page_id
			  AND min_classification = @classification
			  AND COALESCE(persona_id, '00000000-0000-0000-0000-000000000000'::uuid)
			    = COALESCE(@persona_id, '00000000-0000-0000-0000-000000000000'::uuid)
			""";

		await using (var facetCmd = new NpgsqlCommand(facetUpdate, conn, tx))
		{
			facetCmd.Parameters.AddWithValue("rev_id", revisionId);
			facetCmd.Parameters.AddWithValue("body", draft.BodyMarkdown);
			facetCmd.Parameters.AddWithValue("page_id", draft.PageId);
			facetCmd.Parameters.AddWithValue("classification", draft.MinClassification.ToString());
			facetCmd.Parameters.AddWithValue("persona_id", (object?)draft.PersonaId ?? DBNull.Value);
			var rowsAffected = await facetCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			if (rowsAffected == 0)
			{
				throw new InvalidOperationException(
					$"page_facets row for page {draft.PageId:D} / {draft.MinClassification} / persona {draft.PersonaId} does not exist. "
					+ "Create the facet row before committing its first revision.");
			}
		}

		// 4. wiki_pages.updated_at bump.
		const string pageBump = "UPDATE wiki_pages SET updated_at = now() WHERE id = @page_id";
		await using (var pageCmd = new NpgsqlCommand(pageBump, conn, tx))
		{
			pageCmd.Parameters.AddWithValue("page_id", draft.PageId);
			await pageCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		return revisionId;
	}
}
