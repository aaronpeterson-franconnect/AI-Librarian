namespace AiLibrarian.Portal.Services;

/// <summary>
/// Strongly-typed access to the Librarian API. Razor components
/// inject this and stay free of <see cref="HttpClient"/> details — when
/// the Phase 2 portal grows, swapping in a Refit / generated client
/// becomes a one-class change.
/// </summary>
public interface IAiLibrarianApiClient
{
	/// <summary>Upload a source file plus its metadata to the API.</summary>
	Task<UploadOutcome> UploadAsync(
		Stream content,
		string fileName,
		string? contentType,
		Guid departmentId,
		string classification,
		string? title,
		Guid? contributorId,
		CancellationToken cancellationToken = default);

	/// <summary>List sources visible to the caller, most-recent first.</summary>
	Task<SourcesPage> ListSourcesAsync(
		Guid? departmentId,
		int limit,
		int offset,
		CancellationToken cancellationToken = default);

	/// <summary>Fetch a single source by id; null when missing or RLS-hidden.</summary>
	Task<SourceListItem?> GetSourceAsync(Guid sourceId, CancellationToken cancellationToken = default);

	/// <summary>List active departments visible to the caller.</summary>
	Task<IReadOnlyList<DepartmentListItem>> ListDepartmentsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Outcome of <see cref="IAiLibrarianApiClient.UploadAsync"/>.</summary>
/// <param name="Success">True when the API returned 2xx and a source row was created.</param>
/// <param name="StatusCode">Raw HTTP status from the API.</param>
/// <param name="Message">Human-readable error message; empty on success.</param>
/// <param name="SourceId">The new source id when <see cref="Success"/> is true.</param>
/// <param name="BlobUri">The blob URI reported by the API.</param>
/// <param name="Title">The title the API stored.</param>
/// <param name="EnqueueAttempted">
/// True when the client tried to chain <c>POST /api/ingest/enqueue</c>
/// after a successful upload. False when upload itself failed (no point
/// queuing a source that doesn't exist).
/// </param>
/// <param name="EnqueueSucceeded">
/// True when the chained enqueue returned 2xx. When upload succeeded but
/// this is false, the source row exists but the worker won't process it
/// until something else enqueues it (manual <c>POST /api/ingest/enqueue</c>
/// or re-running the Portal upload with a retry). Common cause: Service
/// Bus connection string missing on the API.
/// </param>
/// <param name="EnqueueMessage">Human-readable enqueue status / error.</param>
public sealed record UploadOutcome(
	bool Success,
	int StatusCode,
	string Message,
	Guid? SourceId,
	string? BlobUri,
	string? Title,
	bool EnqueueAttempted = false,
	bool EnqueueSucceeded = false,
	string EnqueueMessage = "");

/// <summary>Page of source list items + total count when the API supplies it.</summary>
public sealed record SourcesPage(IReadOnlyList<SourceListItem> Items);

/// <summary>One row in a source listing — projection of the API response.</summary>
public sealed record SourceListItem(
	Guid Id,
	Guid DepartmentId,
	string Classification,
	string Status,
	string Title,
	string? Uri,
	string ContentType,
	string? ChecksumSha256,
	long? SizeBytes,
	Guid ContributedBy,
	Guid? ApprovedBy,
	DateTimeOffset? ApprovedAt,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);

/// <summary>One row in a department listing.</summary>
public sealed record DepartmentListItem(Guid Id, string Name, string DisplayName);
