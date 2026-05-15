using AiLibrarian.Domain.Wiki;
using AiLibrarian.Infrastructure.Rls;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// Postgres <see cref="IWikiPageWriter"/>. Idempotently materializes
/// the <c>wiki_pages</c> + <c>page_facets</c> rows that the on-demand
/// maintenance endpoint and the auto-page-discovery endpoint need.
///
/// <para>Runs in the system admin RLS context so the wiki write
/// policy (<c>app_is_admin()</c> from <c>0102-wiki-rls.sql</c>) passes
/// regardless of the originating caller's session. The handler at the
/// API layer is what authorizes the caller (Admin-only) — this writer
/// is the "system actor materializes a row" backend.</para>
///
/// <para>The page insert uses
/// <c>ON CONFLICT (department_id, slug) DO NOTHING RETURNING id</c>;
/// when the conflict short-circuits we follow up with a SELECT to learn
/// the existing row's id. Both branches return the id without ever
/// overwriting the title or locked flag — operators can rename / lock
/// pages by other means; this endpoint stays additive only.</para>
///
/// <para>The facet insert uses
/// <c>ON CONFLICT DO NOTHING RETURNING ...</c> against the composite
/// primary key <c>(page_id, min_classification, persona_pk)</c>, where
/// <c>persona_pk</c> is a STORED generated column that collapses NULL
/// to the all-zero sentinel UUID (see migration 0021 -- Postgres
/// rejects expressions inside PRIMARY KEY / UNIQUE constraint clauses,
/// so the generated column is how the COALESCE pattern gets enforced).
/// A missing return row means the facet already existed.</para>
/// </summary>
public sealed class PostgresWikiPageWriter : IWikiPageWriter
{
	private readonly NpgsqlDataSource _dataSource;
	private readonly ILogger<PostgresWikiPageWriter> _logger;

	/// <summary>Creates the writer.</summary>
	public PostgresWikiPageWriter(
		NpgsqlDataSource dataSource,
		ILogger<PostgresWikiPageWriter> logger)
	{
		_dataSource = dataSource;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<EnsurePageResult> EnsurePageAsync(
		EnsurePageRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (request.DepartmentId == Guid.Empty)
		{
			throw new ArgumentException("DepartmentId is required.", nameof(request));
		}
		if (!WikiSlug.IsValid(request.Slug))
		{
			throw new ArgumentException(
				$"Slug \"{request.Slug}\" violates the wiki_pages.slug check constraint (^[a-z0-9][a-z0-9\\-]{{0,254}}$).",
				nameof(request));
		}
		if (string.IsNullOrWhiteSpace(request.Title))
		{
			throw new ArgumentException("Title is required.", nameof(request));
		}

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		// Step 1: page. Insert-or-find on (department_id, slug). The
		// ON CONFLICT target points at the partial unique index
		// ux_wiki_pages_dept_slug_live (from migration 0027) so a
		// soft-deleted page with the same slug does NOT block creating
		// a fresh row -- slug reuse after soft-delete is the documented
		// workflow.
		Guid pageId;
		bool pageCreated;
		const string insertPageSql = """
			INSERT INTO wiki_pages (department_id, slug, title)
			VALUES (@dept, @slug, @title)
			ON CONFLICT (department_id, slug) WHERE soft_deleted_at IS NULL DO NOTHING
			RETURNING id
			""";
		await using (var insertCmd = new NpgsqlCommand(insertPageSql, conn, tx))
		{
			insertCmd.Parameters.AddWithValue("dept", request.DepartmentId);
			insertCmd.Parameters.AddWithValue("slug", request.Slug);
			insertCmd.Parameters.AddWithValue("title", request.Title);
			var inserted = await insertCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (inserted is Guid newId)
			{
				pageId = newId;
				pageCreated = true;
			}
			else
			{
				// Conflict path -- look up the existing LIVE row's id.
				// (Soft-deleted rows with the same slug are ignored;
				// the partial unique index already proved no live row
				// exists with this slug except the one we conflicted
				// against.)
				const string selectPageSql = """
					SELECT id FROM wiki_pages
					WHERE department_id = @dept AND slug = @slug AND soft_deleted_at IS NULL
					""";
				await using var selectCmd = new NpgsqlCommand(selectPageSql, conn, tx);
				selectCmd.Parameters.AddWithValue("dept", request.DepartmentId);
				selectCmd.Parameters.AddWithValue("slug", request.Slug);
				var existing = await selectCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
				if (existing is not Guid existingId)
				{
					// Should be impossible: insert said conflict, select says missing.
					throw new InvalidOperationException(
						$"wiki_pages row for ({request.DepartmentId}, {request.Slug}) is neither insertable nor selectable. Check RLS policy.");
				}
				pageId = existingId;
				pageCreated = false;
			}
		}

		// Step 2: facet. Insert-or-leave on (page_id, classification, persona).
		bool facetCreated;
		// Conflict target is the column triple of the composite PK; the
		// generated persona_pk column on page_facets is what carries the
		// sentinel-collapsed persona key. Postgres won't accept the
		// COALESCE expression as the conflict_target here even though
		// the underlying value is identical -- the conflict_target must
		// match the index column list verbatim.
		const string insertFacetSql = """
			INSERT INTO page_facets (page_id, min_classification, persona_id, body_markdown)
			VALUES (@page, @class, @persona, '')
			ON CONFLICT (page_id, min_classification, persona_pk)
			DO NOTHING
			RETURNING page_id
			""";
		await using (var facetCmd = new NpgsqlCommand(insertFacetSql, conn, tx))
		{
			facetCmd.Parameters.AddWithValue("page", pageId);
			facetCmd.Parameters.AddWithValue("class", request.FacetClassification.ToString());
			if (request.PersonaId is Guid persona)
			{
				facetCmd.Parameters.AddWithValue("persona", persona);
			}
			else
			{
				facetCmd.Parameters.AddWithValue("persona", DBNull.Value);
			}
			var facetResult = await facetCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			facetCreated = facetResult is Guid;
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		_logger.LogInformation(
			"EnsurePage dept={Dept} slug={Slug} page={PageId} pageCreated={PageCreated} facet={Classification}/{Persona} facetCreated={FacetCreated}",
			request.DepartmentId,
			request.Slug,
			pageId,
			pageCreated,
			request.FacetClassification,
			request.PersonaId,
			facetCreated);

		return new EnsurePageResult(pageId, pageCreated, facetCreated);
	}

	/// <inheritdoc />
	public async Task<bool> RenameAsync(
		Guid pageId,
		string newTitle,
		CancellationToken cancellationToken = default)
	{
		if (pageId == Guid.Empty)
		{
			throw new ArgumentException("PageId is required.", nameof(pageId));
		}
		if (string.IsNullOrWhiteSpace(newTitle))
		{
			throw new ArgumentException("Title is required.", nameof(newTitle));
		}

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		// updated_at is bumped explicitly so observers can see the rename.
		// soft-deleted pages are not renameable -- the operator should
		// undo the soft-delete first if they want to keep editing.
		const string sql = """
			UPDATE wiki_pages
			   SET title = @title, updated_at = now()
			 WHERE id = @id AND soft_deleted_at IS NULL
			""";
		int rows;
		await using (var cmd = new NpgsqlCommand(sql, conn, tx))
		{
			cmd.Parameters.AddWithValue("title", newTitle);
			cmd.Parameters.AddWithValue("id", pageId);
			rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		_logger.LogInformation(
			"Rename page={PageId} updated_rows={Rows} (true when 1, false when page missing)",
			pageId, rows);

		return rows == 1;
	}

	/// <inheritdoc />
	public async Task<bool> SetLockedAsync(
		Guid pageId,
		bool locked,
		CancellationToken cancellationToken = default)
	{
		if (pageId == Guid.Empty)
		{
			throw new ArgumentException("PageId is required.", nameof(pageId));
		}

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		// updated_at bumps even when the flag is already at the requested
		// value, so the lock-history audit trail is monotonic. We do
		// NOT touch soft-deleted pages -- the WHERE clause filters them
		// out so the call returns false (treated as 404 at the API).
		const string sql = """
			UPDATE wiki_pages
			   SET locked = @locked, updated_at = now()
			 WHERE id = @id AND soft_deleted_at IS NULL
			""";
		int rows;
		await using (var cmd = new NpgsqlCommand(sql, conn, tx))
		{
			cmd.Parameters.AddWithValue("locked", locked);
			cmd.Parameters.AddWithValue("id", pageId);
			rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		_logger.LogInformation(
			"SetLocked page={PageId} locked={Locked} updated_rows={Rows}",
			pageId, locked, rows);

		return rows == 1;
	}

	/// <inheritdoc />
	public async Task<RestorePageResult> RestoreAsync(
		Guid pageId,
		CancellationToken cancellationToken = default)
	{
		if (pageId == Guid.Empty)
		{
			throw new ArgumentException("PageId is required.", nameof(pageId));
		}

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		// Step 1: look up the soft-deleted row (the row we want to restore).
		Guid departmentId;
		string slug;
		await using (var lookupCmd = new NpgsqlCommand(
			"SELECT department_id, slug FROM wiki_pages WHERE id = @id AND soft_deleted_at IS NOT NULL",
			conn, tx))
		{
			lookupCmd.Parameters.AddWithValue("id", pageId);
			await using var reader = await lookupCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
				_logger.LogInformation("Restore page={PageId} NotFound (id unknown or already live)", pageId);
				return new RestorePageResult(RestorePageOutcome.NotFound, ConflictingLivePageId: null);
			}
			departmentId = reader.GetGuid(0);
			slug = reader.GetString(1);
		}

		// Step 2: check for a live conflict on (department_id, slug).
		// The partial unique index ux_wiki_pages_dept_slug_live makes
		// this a hard constraint at UPDATE time; checking here lets
		// the API return a clean 409 with the conflicting id rather
		// than surfacing a unique-violation 23505 stack trace.
		Guid? conflictingId = null;
		await using (var conflictCmd = new NpgsqlCommand(
			"SELECT id FROM wiki_pages WHERE department_id = @d AND slug = @s AND soft_deleted_at IS NULL",
			conn, tx))
		{
			conflictCmd.Parameters.AddWithValue("d", departmentId);
			conflictCmd.Parameters.AddWithValue("s", slug);
			var result = await conflictCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (result is Guid existingLive)
			{
				conflictingId = existingLive;
			}
		}

		if (conflictingId is Guid conflict)
		{
			await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
			_logger.LogInformation(
				"Restore page={PageId} SlugConflict; live row {Conflicting} holds (dept={Dept}, slug={Slug})",
				pageId, conflict, departmentId, slug);
			return new RestorePageResult(RestorePageOutcome.SlugConflict, ConflictingLivePageId: conflict);
		}

		// Step 3: clear soft_deleted_at and bump updated_at.
		const string restoreSql = """
			UPDATE wiki_pages
			   SET soft_deleted_at = NULL, updated_at = now()
			 WHERE id = @id AND soft_deleted_at IS NOT NULL
			""";
		await using (var restoreCmd = new NpgsqlCommand(restoreSql, conn, tx))
		{
			restoreCmd.Parameters.AddWithValue("id", pageId);
			var rows = await restoreCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			if (rows != 1)
			{
				// Should be impossible: we held the soft-deleted state
				// inside the transaction. A concurrent restore would
				// land here.
				await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
				_logger.LogWarning(
					"Restore page={PageId}: UPDATE affected {Rows} rows; suspected concurrent restore. Reporting NotFound.",
					pageId, rows);
				return new RestorePageResult(RestorePageOutcome.NotFound, ConflictingLivePageId: null);
			}
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
		_logger.LogInformation("Restore page={PageId} Restored", pageId);
		return new RestorePageResult(RestorePageOutcome.Restored, ConflictingLivePageId: null);
	}

	/// <inheritdoc />
	public async Task<bool> SoftDeleteAsync(
		Guid pageId,
		CancellationToken cancellationToken = default)
	{
		if (pageId == Guid.Empty)
		{
			throw new ArgumentException("PageId is required.", nameof(pageId));
		}

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		// Idempotent: filter on soft_deleted_at IS NULL so re-deleting a
		// row is a no-op (returns 0 rows updated). Same for missing
		// pages -- both paths surface as "false" to the caller, which
		// the API translates to 404.
		const string sql = """
			UPDATE wiki_pages
			   SET soft_deleted_at = now(), updated_at = now()
			 WHERE id = @id AND soft_deleted_at IS NULL
			""";
		int rows;
		await using (var cmd = new NpgsqlCommand(sql, conn, tx))
		{
			cmd.Parameters.AddWithValue("id", pageId);
			rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		_logger.LogInformation(
			"SoftDelete page={PageId} updated_rows={Rows} (false = missing-or-already-deleted)",
			pageId, rows);

		return rows == 1;
	}
}
