using System.Security.Claims;

using AiLibrarian.Auditing;

namespace AiLibrarian.Api.Telemetry;

/// <summary>
/// Helpers for emitting <see cref="AuditEvent"/> rows from minimal-API
/// route handlers without each endpoint hand-rolling the same record
/// shape. Kept internal — the public contract is
/// <see cref="IAuditWriter"/>.
/// </summary>
internal static class RouteAuditing
{
	/// <summary>
	/// Build a route-success audit event. Caller identity is derived
	/// from the principal's <c>oid</c> claim when present; falls back
	/// to <see cref="AuditConstants.SystemUserId"/> for
	/// no-Entra dev mode (the audit row still lands so the ledger has
	/// continuity, but the actor is the system sentinel).
	/// </summary>
	public static AuditEvent Build(
		ClaimsPrincipal? user,
		Guid correlationId,
		string eventType,
		string eventSubtype,
		string? targetKind,
		Guid? targetId,
		EventOutcome outcome,
		string? errorClass = null,
		IReadOnlyDictionary<string, object?>? details = null)
	{
		return new AuditEvent(
			Id: Guid.NewGuid(),
			OccurredAt: DateTimeOffset.UtcNow,
			ActorUserId: TryGetActorOid(user) ?? AuditConstants.SystemUserId,
			ActorRole: null,
			OriginatedBy: null,
			DepartmentId: null,
			EventType: eventType,
			EventSubtype: eventSubtype,
			TargetKind: targetKind,
			TargetId: targetId,
			CorrelationId: correlationId,
			Outcome: outcome,
			ErrorClass: errorClass,
			Llm: null,
			Details: details ?? new Dictionary<string, object?>());
	}

	/// <summary>
	/// Read the <c>oid</c> claim when present (Entra surfaces the
	/// stable user object identifier as <c>oid</c>; some token
	/// configurations expose it as <c>http://schemas.microsoft.com/identity/claims/objectidentifier</c>).
	/// </summary>
	private static Guid? TryGetActorOid(ClaimsPrincipal? user)
	{
		if (user is null)
		{
			return null;
		}

		var raw = user.FindFirstValue("oid")
			?? user.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier")
			?? user.FindFirstValue(ClaimTypes.NameIdentifier);

		return Guid.TryParse(raw, out var parsed) ? parsed : null;
	}
}
