namespace AiLibrarian.Api.EntraSync;

/// <summary>
/// Stub <see cref="IGraphMembershipClient"/> used when the real Graph
/// client isn't viable — typically because <see cref="EntraGroupSyncOptions.Enabled"/>
/// is false or the tenant / client / secret triple is incomplete.
///
/// <para>Always returns an empty membership list. The orchestrator's
/// reconcile pass then deletes every grant tagged with the
/// <c>entra-sync:</c> source-group prefix for that group, which is the
/// correct behavior when the operator has disabled / misconfigured the
/// sync — the desired state is "no sync-managed grants."</para>
///
/// <para>Hot-flip from real → stub is intentional: the DI registration
/// is decided at startup, so toggling <c>EntraSync:Enabled</c> requires
/// an API restart. This is a deliberate trade-off for simplicity.</para>
/// </summary>
internal sealed class NoopGraphMembershipClient : IGraphMembershipClient
{
	public Task<IReadOnlyList<Guid>> ListGroupMemberOidsAsync(string groupObjectId, CancellationToken cancellationToken = default)
		=> Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());
}
