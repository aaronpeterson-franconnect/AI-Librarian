namespace AiLibrarian.Infrastructure.Retrieval;

/// <summary>Per-request hybrid search parameters (after HTTP validation).</summary>
public sealed record HybridSearchRequestOptions(int Limit, double VectorWeight);
