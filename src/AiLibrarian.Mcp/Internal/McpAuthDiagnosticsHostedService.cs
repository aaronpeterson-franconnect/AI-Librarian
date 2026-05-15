using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiLibrarian.Mcp.Internal;

/// <summary>Startup log line for workstation auth — no token value or raw claims logged.</summary>
internal sealed class McpAuthDiagnosticsHostedService : IHostedService
{
	private readonly McpWorkstationContext _context;
	private readonly ILogger<McpAuthDiagnosticsHostedService> _logger;

	public McpAuthDiagnosticsHostedService(
		McpWorkstationContext context,
		ILogger<McpAuthDiagnosticsHostedService> logger)
	{
		_context = context;
		_logger = logger;
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		if (!_context.HasBearerToken)
		{
			_logger.LogInformation(
				"MCP workstation mode: no AILIB_ACCESS_TOKEN (anonymous); use \"ailib login\" then \"ailib mcp\" for an authenticated session.");
			return Task.CompletedTask;
		}

		if (!_context.ParsedClaims)
		{
			_logger.LogWarning(
				"AILIB_ACCESS_TOKEN set but JWT claims could not be read; tools still run but caller identity is unavailable.");
			return Task.CompletedTask;
		}

		_logger.LogInformation(
			"MCP workstation mode: bearer token present (Entra oid available: {HasOid}, tid available: {HasTid}).",
			!string.IsNullOrEmpty(_context.EntraObjectId),
			!string.IsNullOrEmpty(_context.DirectoryTenantId));
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
