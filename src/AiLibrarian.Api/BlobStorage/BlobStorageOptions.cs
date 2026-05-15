namespace AiLibrarian.Api.BlobStorage;

/// <summary>Azure Blob Storage for portal uploads (Phase 1).</summary>
public sealed class BlobStorageOptions
{
	public const string SectionName = "BlobStorage";

	/// <summary>Storage account connection string (e.g. Azurite or Azure).</summary>
	public string ConnectionString { get; set; } = "";

	/// <summary>Container name; must match ingest pipeline expectations (default <c>sources</c> per Bicep).</summary>
	public string ContainerName { get; set; } = "sources";
}
