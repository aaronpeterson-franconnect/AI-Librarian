namespace AiLibrarian.Api.EntraSync;

/// <summary>
/// Thin abstraction over the single Microsoft Graph call the sync job
/// needs: "list members of group X." Exposed as an interface so the
/// orchestrator's reconciliation logic can be unit-tested without
/// reaching the network.
/// </summary>
public interface IGraphMembershipClient
{
	/// <summary>
	/// Return the OIDs of every direct member of the supplied Entra
	/// group. The Graph response paginates; the implementation
	/// follows <c>@odata.nextLink</c> until the full set is returned.
	/// Transitive membership (members of nested groups) is not
	/// followed; declare the leaf groups explicitly in
	/// <see cref="EntraGroupSyncOptions.GroupMappings"/>.
	/// </summary>
	Task<IReadOnlyList<Guid>> ListGroupMemberOidsAsync(
		string groupObjectId,
		CancellationToken cancellationToken = default);
}
