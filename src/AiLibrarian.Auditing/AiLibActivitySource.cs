using System.Diagnostics;

namespace AiLibrarian.Auditing;

/// <summary>
/// Centralized <see cref="ActivitySource"/> registry. Every span the
/// platform emits opens off one of these names, so an OpenTelemetry
/// pipeline can subscribe with a single
/// <c>AddSource(AiLibActivitySource.Names.All)</c> at registration
/// time. Sharing this contract from <c>AiLibrarian.Auditing</c> keeps
/// the audit ledger and the trace stream addressable under the same
/// correlation id without forcing a new infrastructure project.
///
/// <para>
/// Naming convention is <c>ailib.&lt;area&gt;[.&lt;sub&gt;]</c>,
/// matching the audit-event <c>event_type / event_subtype</c> shape.
/// Pin attribute names with <see cref="Attributes"/> so dashboards
/// don't drift away from the canonical schema documented in
/// <c>docs/observability/tracing-schema.md</c>.
/// </para>
/// </summary>
public static class AiLibActivitySource
{
	/// <summary>Hybrid retrieval (vector + full-text).</summary>
	public static readonly ActivitySource Search = new(Names.Search);

	/// <summary>Ingest-pipeline stages: blob open, canonicalize, persist, embed.</summary>
	public static readonly ActivitySource Ingest = new(Names.Ingest);

	/// <summary>LLM gateway calls — chat, embedding, rerank.</summary>
	public static readonly ActivitySource Llm = new(Names.Llm);

	/// <summary>Audit-ledger writes.</summary>
	public static readonly ActivitySource Audit = new(Names.Audit);

	/// <summary>MCP tool invocations.</summary>
	public static readonly ActivitySource Mcp = new(Names.Mcp);

	/// <summary>Source / department / chunk repository reads.</summary>
	public static readonly ActivitySource Repository = new(Names.Repository);

	/// <summary>
	/// Stable string names — what a tracing subscriber actually
	/// registers. Public so DI helpers can pass <see cref="All"/>
	/// to <c>AddSource</c> without reflecting over the readonly
	/// fields above.
	/// </summary>
	public static class Names
	{
		public const string Search = "AiLibrarian.Search";
		public const string Ingest = "AiLibrarian.Ingest";
		public const string Llm = "AiLibrarian.Llm";
		public const string Audit = "AiLibrarian.Audit";
		public const string Mcp = "AiLibrarian.Mcp";
		public const string Repository = "AiLibrarian.Repository";

		/// <summary>Every <see cref="ActivitySource"/> name registered by this module.</summary>
		public static readonly string[] All = [Search, Ingest, Llm, Audit, Mcp, Repository];
	}

	/// <summary>
	/// Canonical attribute keys — pin these from one place so the
	/// trace pipeline stays parseable as the system grows. The schema
	/// at <c>docs/observability/tracing-schema.md</c> is the
	/// human-readable companion.
	/// </summary>
	public static class Attributes
	{
		public const string UserOidHashed = "ailib.user.oid_hash";
		public const string DepartmentId = "ailib.dept.id";
		public const string PersonaId = "ailib.persona.id";
		public const string ClassificationMax = "ailib.classification.max";
		public const string CorrelationId = "ailib.correlation_id";

		public const string SkillName = "ailib.skill.name";
		public const string SourceId = "ailib.source.id";
		public const string ChunkCount = "ailib.chunk.count";
		public const string ChecksumSha256 = "ailib.source.sha256";

		public const string LlmProvider = "ailib.llm.provider";
		public const string LlmModel = "ailib.llm.model";
		public const string LlmTokensIn = "ailib.llm.tokens.in";
		public const string LlmTokensOut = "ailib.llm.tokens.out";
		public const string LlmLatencyMs = "ailib.llm.latency_ms";

		public const string AuditEventType = "ailib.audit.event_type";
		public const string AuditEventSubtype = "ailib.audit.event_subtype";
		public const string AuditCriticality = "ailib.audit.criticality";

		public const string McpToolName = "ailib.mcp.tool";
		public const string SearchHitCount = "ailib.search.hit_count";
	}
}
