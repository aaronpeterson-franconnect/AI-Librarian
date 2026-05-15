using AiLibrarian.Infrastructure.Rls;

namespace AiLibrarian.IngestWorker;

public sealed class IngestWorkerOptions
{
	public const string SectionName = "IngestWorker";

	public ServiceBusIngestOptions? ServiceBus { get; set; }

	public IngestProcessingOptions Processing { get; set; } = new();

	public IngestBlobOptions Blob { get; set; } = new();

	public IngestWorkerDatabaseOptions Database { get; set; } = new();

	public IngestEmbeddingsOptions Embeddings { get; set; } = new();
}

public sealed class ServiceBusIngestOptions
{
	/// <summary>Azure Service Bus connection string; empty disables the subscriber (dev).</summary>
	public string? ConnectionString { get; set; }

	/// <summary>Queue for ingest job messages.</summary>
	public string IngestQueueName { get; set; } = "ingest-jobs";
}

public sealed class IngestProcessingOptions
{
	/// <summary>
	/// When false (default), only validate queue JSON and log skill resolution — no blob download or DB writes.
	/// Set true when Azure Blob + optional Postgres are available.
	/// </summary>
	public bool RunContentPipeline { get; set; }

	/// <summary>When true, runs embedding after chunks are persisted (requires <c>LlmGateway</c> + Azure OpenAI embedding deployment).</summary>
	public bool GenerateEmbeddings { get; set; }
}

/// <summary>Embedding deployment name and vector dimension (must match <c>db/changelog/0015-source-chunks-embedding.sql</c>).</summary>
public sealed class IngestEmbeddingsOptions
{
	/// <summary>Azure OpenAI embedding deployment name (same as the model id passed to the embedding API).</summary>
	public string ModelDeploymentName { get; set; } = "";

	public int ExpectedDimensions { get; set; } = 1536;
}

public sealed class IngestBlobOptions
{
	/// <summary>Optional Azurite / dev storage connection string. When empty and <see cref="UseManagedIdentity"/> is true, uses DefaultAzureCredential against the blob URI.</summary>
	public string? ConnectionString { get; set; }

	public bool UseManagedIdentity { get; set; } = true;
}

public sealed class IngestWorkerDatabaseOptions
{
	public string? ConnectionString { get; set; }

	public IngestRlsOptions Rls { get; set; } = new();
}

/// <summary>Session variables pushed on each ingest DB transaction (ADR 0005). Must match the principal allowed to write chunks for the target source.</summary>
public sealed class IngestRlsOptions
{
	public Guid UserId { get; set; }

	public bool IsAuthenticated { get; set; } = true;

	public bool IsEmployee { get; set; } = true;

	public Guid[] HomeDepartmentIds { get; set; } = [];

	public Guid[] ContributorDepartmentIds { get; set; } = [];

	public Guid[] ReviewerDepartmentIds { get; set; } = [];

	public Guid[] LibrarianDepartmentIds { get; set; } = [];

	public bool IsAdmin { get; set; }

	public Guid? PersonaId { get; set; }

	public RlsSessionContext ToRlsSessionContext() => new(
		UserId,
		IsAuthenticated,
		IsEmployee,
		HomeDepartmentIds,
		ContributorDepartmentIds,
		ReviewerDepartmentIds,
		LibrarianDepartmentIds,
		IsAdmin,
		PersonaId);
}
