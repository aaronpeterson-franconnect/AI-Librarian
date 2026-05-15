using System.Text.Json;

using AiLibrarian.Domain.Ingest;

using Azure.Messaging.ServiceBus;

using Microsoft.Extensions.Options;

namespace AiLibrarian.Api.Ingest;

public sealed class ServiceBusIngestJobPublisher : IIngestJobPublisher
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = false,
	};

	private readonly ServiceBusClient _client;
	private readonly IOptions<IngestQueueOptions> _options;

	public ServiceBusIngestJobPublisher(ServiceBusClient client, IOptions<IngestQueueOptions> options)
	{
		_client = client;
		_options = options;
	}

	public async Task<PublishIngestJobResult> PublishAsync(IngestJobMessage job, CancellationToken cancellationToken)
	{
		var json = JsonSerializer.Serialize(job, JsonOptions);
		await using var sender = _client.CreateSender(_options.Value.QueueName);
		var msg = new ServiceBusMessage(BinaryData.FromString(json))
		{
			ContentType = "application/json",
		};
		await sender.SendMessageAsync(msg, cancellationToken).ConfigureAwait(false);
		return new PublishIngestJobResult(MessageId: msg.MessageId);
	}
}
