namespace AiLibrarian.Security;

/// <summary>
/// Knobs for <see cref="AskGuard"/>. Defaults match ADR 0017; per-tenant
/// overrides flow in via the standard options pipeline.
/// </summary>
public sealed class AskGuardOptions
{
	/// <summary>Configuration section name (<c>Mcp:AskGuard</c>).</summary>
	public const string SectionName = "Mcp:AskGuard";

	/// <summary>Maximum query byte length. Default 4096 (UTF-8 bytes).</summary>
	public int MaxQueryBytes { get; set; } = 4096;

	/// <summary>Token-bucket rate limit per Entra OID, calls per minute. Default 20.</summary>
	public int RateLimitPerMinutePerCaller { get; set; } = 20;

	/// <summary>
	/// Output redaction mode. <c>Shadow</c> (default) logs candidates
	/// without altering output; <c>Enforce</c> replaces matched runs
	/// with <c>[REDACTED:&lt;kind&gt;]</c>; <c>Off</c> disables the
	/// scan entirely (development only).
	/// </summary>
	public SecretRedactionMode RedactionMode { get; set; } = SecretRedactionMode.Shadow;
}

/// <summary>Operating modes for the secret redactor.</summary>
public enum SecretRedactionMode
{
	/// <summary>No redaction; no scan. Development use only.</summary>
	Off = 0,

	/// <summary>Scan + audit + return original output unchanged. Phase 1 default.</summary>
	Shadow = 1,

	/// <summary>Scan + audit + return output with matches replaced. Per-tenant flip after precision sampling.</summary>
	Enforce = 2,
}
