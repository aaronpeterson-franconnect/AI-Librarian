using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiLibrarian.Api.EntraSync;

/// <summary>
/// Periodic runner for <see cref="EntraGroupSyncService"/>. Off when
/// <see cref="EntraGroupSyncOptions.Enabled"/> is false (the hosted
/// service still registers but the inner loop just waits for the
/// stopping token).
///
/// <para>Doesn't run on startup — Container Apps probes the API
/// before traffic starts; the first sync happens after one full
/// interval. The admin endpoint <c>POST /api/admin/entra-sync</c>
/// covers the "I want to seed roles right now" case.</para>
/// </summary>
internal sealed class EntraGroupSyncHostedService : BackgroundService
{
	private readonly IServiceProvider _serviceProvider;
	private readonly IOptions<EntraGroupSyncOptions> _options;
	private readonly ILogger<EntraGroupSyncHostedService> _logger;

	public EntraGroupSyncHostedService(
		IServiceProvider serviceProvider,
		IOptions<EntraGroupSyncOptions> options,
		ILogger<EntraGroupSyncHostedService> logger)
	{
		_serviceProvider = serviceProvider;
		_options = options;
		_logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		var opts = _options.Value;
		if (!opts.Enabled)
		{
			_logger.LogInformation("EntraGroupSync hosted service: disabled (EntraSync:Enabled=false).");
			return;
		}

		if (opts.GroupMappings.Count == 0)
		{
			_logger.LogWarning(
				"EntraGroupSync hosted service: enabled but no GroupMappings configured. The loop will run but every pass is a no-op until mappings are added.");
		}

		var interval = opts.Interval < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : opts.Interval;
		_logger.LogInformation("EntraGroupSync hosted service: starting; interval={Interval}.", interval);

		using var timer = new PeriodicTimer(interval);
		try
		{
			while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
			{
				await RunOneTickAsync(stoppingToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
			// graceful shutdown
		}
	}

	private async Task RunOneTickAsync(CancellationToken cancellationToken)
	{
		try
		{
			using var scope = _serviceProvider.CreateScope();
			var sync = scope.ServiceProvider.GetRequiredService<EntraGroupSyncService>();
			var report = await sync.RunAsync(cancellationToken).ConfigureAwait(false);
			_logger.LogInformation(
				"EntraGroupSync tick: groups={Processed} ok={Ok} failed={Failed} added={Added} revoked={Revoked} duration_ms={Duration}",
				report.GroupsProcessed,
				report.GroupsSucceeded,
				report.GroupsFailed,
				report.GrantsAdded,
				report.GrantsRevoked,
				report.DurationMs);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_logger.LogError(ex, "EntraGroupSync tick failed; next tick will retry.");
		}
	}
}
