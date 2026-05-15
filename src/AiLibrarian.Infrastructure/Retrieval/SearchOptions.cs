namespace AiLibrarian.Infrastructure.Retrieval;

/// <summary>Hybrid retrieval tuning bound from configuration section <c>Search:</c>.</summary>
public sealed class SearchOptions
{
	public const string SectionName = "Search";

	/// <summary>Weight applied to the vector similarity branch (text weight is <c>1 - this</c>).</summary>
	public double HybridVectorWeight { get; set; } = 0.6;

	public int DefaultLimit { get; set; } = 15;

	public int MaxLimit { get; set; } = 50;

	/// <summary>Must match <c>source_chunks.embedding</c> dimension (Liquibase <c>0015</c>).</summary>
	public int ExpectedEmbeddingDimensions { get; set; } = 1536;

	/// <summary>Optional override; when empty the API uses <c>LlmGateway:Providers:azure-openai:EmbeddingDeployment</c>.</summary>
	public string? EmbeddingDeployment { get; set; }
}
