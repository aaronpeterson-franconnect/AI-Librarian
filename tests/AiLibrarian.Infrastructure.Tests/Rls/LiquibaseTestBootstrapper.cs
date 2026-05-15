using System.Text;

using Npgsql;

namespace AiLibrarian.Infrastructure.Tests.Rls;

/// <summary>
/// Minimal Liquibase-formatted-SQL replayer for integration tests.
/// Reads every <c>db/changelog/*.sql</c> file in lexical order, strips
/// the directive lines (<c>--liquibase formatted sql</c>,
/// <c>--changeset</c>, <c>--comment</c>, <c>--rollback</c>), and
/// executes the remaining forward SQL against the target connection
/// string. Good enough for testcontainer bootstrap; not a substitute
/// for the production Liquibase runner (no checksum tracking, no
/// changeset selection, no rollback support — the tests just need a
/// fully-migrated schema in front of them).
///
/// <para>
/// Why this rather than running the Liquibase CLI in the container:
/// the CLI needs a JVM in the image, which doubles the testcontainer
/// startup cost and adds a network hop to download Liquibase on each
/// run. Forward-only replay is ~200 ms total against an empty
/// Postgres 16.
/// </para>
///
/// <para>
/// Returns a <i>different</i> connection string than the one passed
/// in: the bootstrap runs as the testcontainer's superuser (needed
/// for <c>CREATE EXTENSION pgvector</c>), then provisions a
/// non-superuser application role and grants it the privileges the
/// production workload identity will hold. Tests connect as that
/// role so RLS predicates apply — superusers carry BYPASSRLS and
/// would silently mask access-control bugs.
/// </para>
/// </summary>
internal static class LiquibaseTestBootstrapper
{
	/// <summary>Application role created after bootstrap; tests connect as this.</summary>
	internal const string AppRoleName = "ailib_app";

	/// <summary>Application role password — set during bootstrap.</summary>
	internal const string AppRolePassword = "ailib_app_pwd";

	/// <summary>
	/// Apply migrations and provision the non-superuser application
	/// role. Returns the connection string tests should use — same
	/// host/database, but authenticated as <see cref="AppRoleName"/>
	/// instead of the superuser.
	/// </summary>
	public static async Task<string> ApplyAsync(
		string changelogDirectory,
		string connectionString,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(changelogDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

		if (!Directory.Exists(changelogDirectory))
		{
			throw new DirectoryNotFoundException($"Changelog directory not found: {changelogDirectory}");
		}

		var files = Directory
			.EnumerateFiles(changelogDirectory, "*.sql", SearchOption.TopDirectoryOnly)
			.OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
			.ToList();

		if (files.Count == 0)
		{
			throw new InvalidOperationException($"No .sql files in {changelogDirectory}.");
		}

		await using var conn = new NpgsqlConnection(connectionString);
		await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

		foreach (var file in files)
		{
			var raw = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
			var sql = ExtractForwardSql(raw);
			if (string.IsNullOrWhiteSpace(sql))
			{
				continue;
			}

			await using var cmd = new NpgsqlCommand(sql, conn);
			try
			{
				await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				throw new InvalidOperationException(
					$"Liquibase test bootstrap failed in {Path.GetFileName(file)}: {ex.Message}",
					ex);
			}
		}

		// CRITICAL: testcontainers' default Postgres user is a SUPERUSER,
		// which carries the BYPASSRLS attribute. RLS predicates are silently
		// skipped for that role — every "blocked" read would come back as
		// allowed and we'd ship a green test suite that proves nothing. PG
		// also forbids self-demotion of SUPERUSER ("permission denied to
		// alter role"), so we provision a separate non-superuser app role
		// here and have tests connect as that role instead. Bootstrap stays
		// as superuser to install pgvector etc.; tests pay the access-
		// control price they would in production.
		await using var roleCmd = new NpgsqlCommand($"""
			DO $$
			BEGIN
				IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{AppRoleName}') THEN
					CREATE ROLE {AppRoleName} LOGIN PASSWORD '{AppRolePassword}' NOSUPERUSER NOBYPASSRLS;
				END IF;
			END
			$$;

			GRANT USAGE ON SCHEMA public TO {AppRoleName};
			GRANT SELECT, INSERT, UPDATE, DELETE
				ON ALL TABLES IN SCHEMA public TO {AppRoleName};
			GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {AppRoleName};
			GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO {AppRoleName};
			""",
			conn);
		await roleCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

		return AppRoleConnectionString(connectionString);
	}

	/// <summary>
	/// Rebuild a connection string targeting the app role provisioned
	/// by <see cref="ApplyAsync"/>. Same host / port / database, just
	/// different credentials so RLS applies.
	/// </summary>
	public static string AppRoleConnectionString(string superuserConnectionString)
	{
		var builder = new NpgsqlConnectionStringBuilder(superuserConnectionString)
		{
			Username = AppRoleName,
			Password = AppRolePassword,
		};
		return builder.ConnectionString;
	}

	/// <summary>
	/// Strip Liquibase directive lines and rollback bodies from a
	/// changelog file, leaving only the forward DDL/DML.
	/// <c>--changeset</c> boundaries are not preserved as transaction
	/// boundaries — every test bootstrap runs the file as one batch.
	/// </summary>
	internal static string ExtractForwardSql(string raw)
	{
		ArgumentNullException.ThrowIfNull(raw);

		var sb = new StringBuilder(raw.Length);
		using var reader = new StringReader(raw);
		string? line;
		while ((line = reader.ReadLine()) is not null)
		{
			var trimmed = line.TrimStart();
			if (trimmed.StartsWith("--liquibase formatted sql", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (trimmed.StartsWith("--changeset", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (trimmed.StartsWith("--comment", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (trimmed.StartsWith("--rollback", StringComparison.OrdinalIgnoreCase))
			{
				// rollback bodies are single-line in this codebase; skip
				// just this line and let the next forward SQL continue.
				continue;
			}

			sb.AppendLine(line);
		}

		return sb.ToString();
	}
}
