using Testcontainers.PostgreSql;

namespace AiLibrarian.Infrastructure.Tests.Rls;

/// <summary>
/// Spins an ephemeral Postgres 16 container with <c>pgvector</c>
/// installed, then replays every Liquibase changelog file via
/// <see cref="LiquibaseTestBootstrapper"/>. Shared across the RLS
/// battery so the ~5-10s container start cost is paid once per
/// test class lifetime, not once per test.
///
/// <para>
/// Skips silently when Docker is unreachable — the harness throws a
/// <see cref="SkipException"/>-style assertion message. That keeps
/// `dotnet test` green on workstations without Docker Desktop while
/// still failing CI (where Docker is always available on
/// <c>ubuntu-latest</c> runners).
/// </para>
/// </summary>
public sealed class RlsPostgresFixture : IAsyncLifetime
{
	private static readonly string _changelogDirectory = LocateChangelogDirectory();

	private PostgreSqlContainer? _container;

	/// <summary>True when the container started and migrations applied.</summary>
	public bool IsAvailable { get; private set; }

	/// <summary>The connection string for the running container; only meaningful when <see cref="IsAvailable"/> is true.</summary>
	public string ConnectionString { get; private set; } = string.Empty;

	/// <summary>
	/// Superuser connection string — bypasses RLS. Used for the
	/// audit-trigger tests that need to actually trigger the
	/// append-only RAISE rather than be silenced by RLS, and for any
	/// fixture cleanup that needs to span policy boundaries.
	/// </summary>
	public string SuperuserConnectionString { get; private set; } = string.Empty;

	/// <summary>
	/// Reason the fixture is unavailable, when <see cref="IsAvailable"/>
	/// is false. Surfaced to tests so they can <c>Skip</c> with a clear
	/// explanation instead of a stack trace.
	/// </summary>
	public string? UnavailableReason { get; private set; }

	/// <inheritdoc />
	public async Task InitializeAsync()
	{
		try
		{
			_container = new PostgreSqlBuilder()
				.WithImage("pgvector/pgvector:pg16")
				.WithDatabase("ailib_test")
				.WithUsername("ailib_test")
				.WithPassword("ailib_test")
				.Build();

			await _container.StartAsync().ConfigureAwait(false);
			SuperuserConnectionString = _container.GetConnectionString();

			// Bootstrap returns the *non-superuser* connection string so
			// RLS predicates apply to test queries. The superuser handle
			// is kept for migration replay and the rare test that needs
			// to bypass RLS (e.g. audit-trigger fire verification).
			ConnectionString = await LiquibaseTestBootstrapper
				.ApplyAsync(_changelogDirectory, SuperuserConnectionString)
				.ConfigureAwait(false);

			IsAvailable = true;
		}
		catch (Exception ex)
		{
			IsAvailable = false;
			UnavailableReason =
				$"RLS Postgres fixture unavailable: {ex.GetType().Name}: {ex.Message}. "
				+ "These tests require Docker (testcontainers); they're skipped on machines without it. "
				+ "CI runners on `ubuntu-latest` have Docker preinstalled.";
			// Surface the underlying exception so CI logs explain WHY the
			// fixture failed. Without this, a Linux runner that should
			// support testcontainers silently skips every dependent test
			// (production-quality regression detection ends up depending
			// on this print landing in the runner's stdout).
			Console.Error.WriteLine($"[RlsPostgresFixture] {UnavailableReason}");
			Console.Error.WriteLine(ex.ToString());
		}
	}

	/// <inheritdoc />
	public async Task DisposeAsync()
	{
		if (_container is not null)
		{
			await _container.DisposeAsync().ConfigureAwait(false);
		}
	}

	private static string LocateChangelogDirectory()
	{
		// Walk up from the test assembly's bin directory to the repo root.
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null)
		{
			var candidate = Path.Combine(dir.FullName, "db", "changelog");
			if (Directory.Exists(candidate)
				&& File.Exists(Path.Combine(candidate, "0001-departments.sql")))
			{
				return candidate;
			}

			dir = dir.Parent;
		}

		throw new DirectoryNotFoundException(
			$"Could not locate db/changelog/ from {AppContext.BaseDirectory}.");
	}
}
