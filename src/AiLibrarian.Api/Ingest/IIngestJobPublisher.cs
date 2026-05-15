using AiLibrarian.Domain.Ingest;

namespace AiLibrarian.Api.Ingest;

public interface IIngestJobPublisher
{
	Task<PublishIngestJobResult> PublishAsync(IngestJobMessage job, CancellationToken cancellationToken);
}
