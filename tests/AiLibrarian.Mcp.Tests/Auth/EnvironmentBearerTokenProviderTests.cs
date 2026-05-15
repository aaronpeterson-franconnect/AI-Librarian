using AiLibrarian.Mcp.Auth;

namespace AiLibrarian.Mcp.Tests.Auth;

/// <summary>
/// Pin the env-provider behavior. The provider re-reads the
/// environment on every call (rather than capturing once), which is
/// the critical difference from the pre-B.8 behavior — even the
/// fallback path can now pick up updated tokens if a parent process
/// rewrites the env var.
/// </summary>
[Collection("EnvironmentMutation")]
public sealed class EnvironmentBearerTokenProviderTests : IDisposable
{
	private readonly string? _originalToken;

	public EnvironmentBearerTokenProviderTests()
	{
		_originalToken = Environment.GetEnvironmentVariable(EnvironmentBearerTokenProvider.EnvironmentVariableName);
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable(EnvironmentBearerTokenProvider.EnvironmentVariableName, _originalToken);
	}

	[Fact]
	public async Task Returns_null_when_env_var_unset()
	{
		Environment.SetEnvironmentVariable(EnvironmentBearerTokenProvider.EnvironmentVariableName, null);
		var provider = new EnvironmentBearerTokenProvider();

		var token = await provider.GetAccessTokenAsync(CancellationToken.None);

		token.Should().BeNull();
	}

	[Fact]
	public async Task Returns_null_when_env_var_whitespace()
	{
		Environment.SetEnvironmentVariable(EnvironmentBearerTokenProvider.EnvironmentVariableName, "   ");
		var provider = new EnvironmentBearerTokenProvider();

		var token = await provider.GetAccessTokenAsync(CancellationToken.None);

		token.Should().BeNull();
	}

	[Fact]
	public async Task Returns_trimmed_token_when_env_var_set()
	{
		Environment.SetEnvironmentVariable(EnvironmentBearerTokenProvider.EnvironmentVariableName, "  eyJhbGc...  ");
		var provider = new EnvironmentBearerTokenProvider();

		var token = await provider.GetAccessTokenAsync(CancellationToken.None);

		token.Should().Be("eyJhbGc...");
	}

	[Fact]
	public async Task Re_reads_env_on_each_call()
	{
		// Critical: pre-B.8 behavior captured the env var once at process
		// start. B.8's contract is that callers always see the latest value.
		var provider = new EnvironmentBearerTokenProvider();

		Environment.SetEnvironmentVariable(EnvironmentBearerTokenProvider.EnvironmentVariableName, "first");
		var first = await provider.GetAccessTokenAsync(CancellationToken.None);

		Environment.SetEnvironmentVariable(EnvironmentBearerTokenProvider.EnvironmentVariableName, "second");
		var second = await provider.GetAccessTokenAsync(CancellationToken.None);

		first.Should().Be("first");
		second.Should().Be("second");
	}
}

/// <summary>
/// xUnit collection scope for tests that mutate process-wide
/// environment variables. Forces serial execution so concurrent
/// fixtures don't observe each other's mutations.
/// </summary>
[CollectionDefinition("EnvironmentMutation", DisableParallelization = true)]
#pragma warning disable CA1711 // xUnit collection-definition naming convention requires the "Collection" suffix.
public sealed class EnvironmentMutationCollection;
#pragma warning restore CA1711
