namespace AiLibrarian.Api.Telemetry;

/// <summary>
/// Scoped accessor for the current request's correlation ID. Resolved
/// from the W3C <c>traceparent</c> header (preferred) or the
/// <c>X-Correlation-Id</c> header on inbound requests; minted by
/// <see cref="CorrelationIdMiddleware"/> if neither is present. Audit
/// writers and downstream HTTP calls pull from this so a single
/// request flows through the system under one identifier — without
/// every route handler hand-rolling <c>Guid.NewGuid()</c>.
/// </summary>
public interface ICorrelationIdAccessor
{
	/// <summary>The current request's correlation ID.</summary>
	Guid Current { get; }
}
