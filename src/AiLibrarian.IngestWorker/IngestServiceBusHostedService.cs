using Azure.Messaging.ServiceBus;

using AiLibrarian.Domain.Skills;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AiLibrarian.IngestWorker;

/// <summary>Subscribes to the ingest queue when configured; otherwise stays idle with skills registered.</summary>
internal sealed class IngestServiceBusHostedService : BackgroundService
{
	private readonly ILogger<IngestServiceBusHostedService> _logger;
	private readonly IOptions<IngestWorkerOptions> _options;
	private readonly ISkillRegistry _skillRegistry;
	private readonly IngestJobPipeline _pipeline;
	private readonly IHostEnvironment _environment;

	public IngestServiceBusHostedService(
		ILogger<IngestServiceBusHostedService> logger,
		IOptions<IngestWorkerOptions> options,
		ISkillRegistry skillRegistry,
		IngestJobPipeline pipeline,
		IHostEnvironment environment)
	{
		_logger = logger;
		_options = options;
		_skillRegistry = skillRegistry;
		_pipeline = pipeline;
		_environment = environment;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		var sb = _options.Value.ServiceBus;
		var skillCount = _skillRegistry.RegisteredSkills.Count;
		if (string.IsNullOrWhiteSpace(sb?.ConnectionString))
		{
			// In Development we tolerate the "no queue" idle mode because
			// unit tests, testcontainers, and quick local repros routinely
			// run the worker without Service Bus wired up. Outside Dev that
			// idle mode is invisibly fatal: the API enqueues messages no
			// consumer reads, sources rows pile up with no chunks, and the
			// portal "(not yet computed)" symptom is indistinguishable from
			// a worker crash. Refuse to start so the operator finds out at
			// startup instead of at first user-visible upload.
			if (!_environment.IsDevelopment())
			{
				var msg =
					$"IngestWorker:ServiceBus:ConnectionString is empty in environment '{_environment.EnvironmentName}'. "
					+ "The worker would silently drop ingest jobs. Set IngestQueue__ConnectionString "
					+ "(or IngestWorker__ServiceBus__ConnectionString) before starting.";
				_logger.LogCritical("{Message}", msg);
				throw new InvalidOperationException(msg);
			}

			_logger.LogInformation(
				"Ingest worker: Service Bus not configured; {SkillCount} skill(s) registered. Waiting (no queue). [Development only]",
				skillCount);
			try
			{
				await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				// shutdown
			}

			return;
		}

		if (string.IsNullOrWhiteSpace(sb.IngestQueueName))
		{
			// Same logic as above: empty queue name is fatal outside Dev.
			if (!_environment.IsDevelopment())
			{
				var msg =
					$"IngestWorker:ServiceBus:IngestQueueName is empty in environment '{_environment.EnvironmentName}'. "
					+ "Worker has a Service Bus connection but no queue to consume from.";
				_logger.LogCritical("{Message}", msg);
				throw new InvalidOperationException(msg);
			}

			_logger.LogWarning("IngestWorker:ServiceBus:IngestQueueName is empty; not starting processor. [Development only]");
			await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
			return;
		}

		await using var client = new ServiceBusClient(sb.ConnectionString);
		await using var processor = client.CreateProcessor(sb.IngestQueueName, new ServiceBusProcessorOptions());
		processor.ProcessMessageAsync += OnMessageAsync;
		processor.ProcessErrorAsync += OnErrorAsync;

		_logger.LogInformation(
			"Ingest worker: listening on queue {Queue} with {SkillCount} skill(s).",
			sb.IngestQueueName,
			skillCount);

		await processor.StartProcessingAsync(stoppingToken).ConfigureAwait(false);
		try
		{
			await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
		}
		finally
		{
			await processor.StopProcessingAsync(stoppingToken).ConfigureAwait(false);
		}
	}

	private Task OnErrorAsync(ProcessErrorEventArgs args)
	{
		_logger.LogError(args.Exception, "Service Bus error source={Source} entity={EntityPath}", args.ErrorSource, args.EntityPath);
		return Task.CompletedTask;
	}

	private async Task OnMessageAsync(ProcessMessageEventArgs args)
	{
		if (!IngestJobMessageReader.TryRead(args.Message.Body, out var job, out var parseError))
		{
			_logger.LogWarning(
				"Ingest message invalid id={MessageId} delivery={Count} error={Error} (dead-letter).",
				args.Message.MessageId,
				args.Message.DeliveryCount,
				parseError);
			await args.DeadLetterMessageAsync(
					args.Message,
					"InvalidPayload",
					parseError ?? "Unknown",
					args.CancellationToken)
				.ConfigureAwait(false);
			return;
		}

		var outcome = await _pipeline.RunAsync(job, args.CancellationToken).ConfigureAwait(false);
		if (!outcome.Ok)
		{
			_logger.LogWarning(
				"Ingest pipeline dead-letter id={MessageId} reason={Reason} detail={Detail}",
				args.Message.MessageId,
				outcome.DeadLetterReason,
				outcome.DeadLetterDescription);
			await args.DeadLetterMessageAsync(
					args.Message,
					outcome.DeadLetterReason ?? "PipelineFailed",
					outcome.DeadLetterDescription ?? string.Empty,
					args.CancellationToken)
				.ConfigureAwait(false);
			return;
		}

		await args.CompleteMessageAsync(args.Message, args.CancellationToken).ConfigureAwait(false);
	}
}
