namespace AiLibrarian.Api.Ingest;

/// <summary>Azure Service Bus queue used to publish JSON <see cref="AiLibrarian.Domain.Ingest.IngestJobMessage"/> bodies.</summary>
public sealed class IngestQueueOptions
{
	public const string SectionName = "IngestQueue";

	public string? ConnectionString { get; set; }

	public string QueueName { get; set; } = "ingest-jobs";
}
