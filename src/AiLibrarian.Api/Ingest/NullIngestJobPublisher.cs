using AiLibrarian.Domain.Ingest;

namespace AiLibrarian.Api.Ingest;

public sealed class NullIngestJobPublisher : IIngestJobPublisher
{
	public Task<PublishIngestJobResult> PublishAsync(IngestJobMessage job, CancellationToken cancellationToken)
		=> Task.FromException<PublishIngestJobResult>(
			new InvalidOperationException("Ingest queue is not configured."));
}
