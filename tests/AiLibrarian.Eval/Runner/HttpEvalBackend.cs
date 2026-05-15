using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiLibrarian.Eval.Runner;

/// <summary>
/// Real-API <see cref="RetrievalBackend"/>. Calls <c>POST /api/search/hybrid</c>
/// for retrieval and (when <see cref="HttpEvalBackendOptions.UseAsk"/> is
/// true) <c>POST /api/ask</c> for synthesis. Use this when the eval
/// harness runs against a live API deployment — typically the nightly
/// CI run against a dev environment, or a manual `dotnet test
/// --filter LiveEval` pass.
///
/// <para>For unit-level evaluation use the stub-backend pattern in
/// <c>EvalRunnerTests</c>. The HTTP backend is the integration shape.</para>
///
/// <para>When <see cref="HttpEvalBackendOptions.UseAsk"/> is false, the
/// backend issues only the retrieval call and reports synthesis metrics
/// as zeros — useful for retrieval-only regression detection (recall@k,
/// MRR, nDCG) without paying LLM tokens on every PR run.</para>
/// </summary>
public sealed class HttpEvalBackend
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	private readonly HttpClient _http;
	private readonly HttpEvalBackendOptions _options;

	/// <summary>Creates the backend. Caller owns the <see cref="HttpClient"/> lifetime.</summary>
	public HttpEvalBackend(HttpClient http, HttpEvalBackendOptions options)
	{
		_http = http ?? throw new ArgumentNullException(nameof(http));
		_options = options ?? throw new ArgumentNullException(nameof(options));

		if (_http.BaseAddress is null && _options.ApiBaseUrl is { Length: > 0 })
		{
			_http.BaseAddress = new Uri(EnsureTrailingSlash(_options.ApiBaseUrl), UriKind.Absolute);
		}
	}

	/// <summary>The <see cref="RetrievalBackend"/> delegate compatible with <see cref="EvalRunner"/>.</summary>
	public RetrievalBackend AsDelegate => RunOneAsync;

	/// <summary>Run one case end-to-end.</summary>
	public async Task<EvalCaseOutcome> RunOneAsync(GoldenCase goldenCase, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(goldenCase);

		var retrievedIds = await RetrieveAsync(goldenCase, cancellationToken).ConfigureAwait(false);

		if (!_options.UseAsk)
		{
			return new EvalCaseOutcome(
				RetrievedChunkIds: retrievedIds,
				ClaimCount: 0,
				CitedClaimCount: 0,
				Refused: false,
				TokensUsed: 0);
		}

		var ask = await AskAsync(goldenCase, cancellationToken).ConfigureAwait(false);
		return new EvalCaseOutcome(
			RetrievedChunkIds: retrievedIds,
			ClaimCount: ask.ClaimCount,
			CitedClaimCount: ask.CitedClaimCount,
			Refused: ask.Refused,
			TokensUsed: ask.TokensUsed);
	}

	private async Task<IReadOnlyList<Guid>> RetrieveAsync(GoldenCase goldenCase, CancellationToken cancellationToken)
	{
		// PersonaId on the request body lets eval runs exercise
		// persona-aware retrieval reranking without touching the
		// caller's session. The API requires Admin to accept the
		// override (debug / eval feature); the backend's bearer
		// token must carry the Admin role for the override to apply.
		using var req = new HttpRequestMessage(HttpMethod.Post, "api/search/hybrid")
		{
			Content = JsonContent.Create(
				new HybridSearchRequest(
					Query: goldenCase.Query,
					Limit: _options.RetrievalLimit,
					VectorWeight: _options.HybridVectorWeight,
					PersonaId: _options.PersonaId),
				options: JsonOptions),
		};
		AddBearer(req);

		using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
		var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		if (!resp.IsSuccessStatusCode)
		{
			throw new InvalidOperationException(
				$"/api/search/hybrid returned {(int)resp.StatusCode} for case {goldenCase.Id}: " +
				(body.Length > 500 ? body[..500] : body));
		}

		var dto = JsonSerializer.Deserialize<HybridSearchResponse>(body, JsonOptions)
			?? throw new InvalidOperationException($"Empty body from /api/search/hybrid for case {goldenCase.Id}.");
		return dto.Hits.Select(h => h.ChunkId).ToArray();
	}

	private async Task<AskOutcome> AskAsync(GoldenCase goldenCase, CancellationToken cancellationToken)
	{
		using var req = new HttpRequestMessage(HttpMethod.Post, "api/ask")
		{
			Content = JsonContent.Create(
				new AskRequest(goldenCase.Query, _options.RetrievalLimit, _options.PersonaId),
				options: JsonOptions),
		};
		AddBearer(req);

		using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
		var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

		// The /api/ask route returns 200 with a refusal payload on cap /
		// no-sources / citation-contract-violated; only rate-limit yields 429.
		if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
		{
			return new AskOutcome(ClaimCount: 0, CitedClaimCount: 0, Refused: true, TokensUsed: 0);
		}

		if (!resp.IsSuccessStatusCode)
		{
			throw new InvalidOperationException(
				$"/api/ask returned {(int)resp.StatusCode} for case {goldenCase.Id}: " +
				(body.Length > 500 ? body[..500] : body));
		}

		var dto = JsonSerializer.Deserialize<AskResponse>(body, JsonOptions);
		if (dto is null)
		{
			return new AskOutcome(0, 0, false, 0);
		}

		// /api/ask now emits a per-sentence claim breakdown
		// (ClaimCount + CitedClaimCount + Claims[]) so we can compute
		// citation coverage without re-parsing the answer here. Older
		// API versions that haven't deployed the breakdown still
		// populate ChunkIds; fall back to the single-claim proxy when
		// ClaimCount is missing.
		var answer = dto.Answer ?? string.Empty;
		var refused = !dto.Admitted || string.IsNullOrWhiteSpace(answer);

		if (refused)
		{
			return new AskOutcome(0, 0, true, 0);
		}

		if (dto.ClaimCount is int claims and > 0)
		{
			return new AskOutcome(
				ClaimCount: claims,
				CitedClaimCount: dto.CitedClaimCount ?? 0,
				Refused: false,
				TokensUsed: 0);
		}

		// Backwards-compat fallback for an API build that hasn't
		// shipped the breakdown yet.
		var hasCitations = dto.ChunkIds is { Count: > 0 };
		return new AskOutcome(
			ClaimCount: 1,
			CitedClaimCount: hasCitations ? 1 : 0,
			Refused: false,
			TokensUsed: 0);
	}

	private void AddBearer(HttpRequestMessage req)
	{
		if (!string.IsNullOrWhiteSpace(_options.BearerToken))
		{
			req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);
		}
	}

	private static string EnsureTrailingSlash(string value)
		=> value.EndsWith('/') ? value : value + "/";

	// --- Wire DTOs. Kept private to avoid coupling consumers to the
	// API's evolving response shape; if/when /api/ask grows a claim
	// breakdown, only this file changes.

	private sealed record HybridSearchRequest(string Query, int? Limit, double? VectorWeight, string? PersonaId);

	private sealed record HybridSearchResponse(Guid CorrelationId, string EmbeddingDeployment, IReadOnlyList<HybridSearchHit> Hits);

	private sealed record HybridSearchHit(
		Guid ChunkId,
		Guid SourceId,
		int OrderIndex,
		string Excerpt,
		double HybridScore,
		double? CosineDistance,
		double? TextRank,
		string? SourceClassification,
		Guid? SourceDepartmentId);

	private sealed record AskRequest(string Query, int? MaxChunks, string? PersonaId);

	private sealed record AskResponse(
		Guid CorrelationId,
		bool Admitted,
		string? Answer,
		AskRefusalDto? Refusal,
		IReadOnlyList<Guid>? ChunkIds,
		int RedactionCandidates,
		string RedactionMode,
		// The next three are present on Phase-1.5+ API builds. They're
		// nullable here so older API responses (without the breakdown)
		// deserialise cleanly and trigger the backwards-compat fallback.
		IReadOnlyList<AskClaimDto>? Claims,
		int? ClaimCount,
		int? CitedClaimCount);

	private sealed record AskClaimDto(string Text, IReadOnlyList<Guid> ChunkIds);

	private sealed record AskRefusalDto(string Reason, string Detail);

	private sealed record AskOutcome(int ClaimCount, int CitedClaimCount, bool Refused, long TokensUsed);

	/// <summary>Format a one-line summary of the report for CI-log output.</summary>
	public static string FormatReportSummary(EvalReport report)
	{
		ArgumentNullException.ThrowIfNull(report);
		return string.Format(
			CultureInfo.InvariantCulture,
			"cases={0} recall@{1}={2:F3} mrr={3:F3} ndcg={4:F3} cov={5:F3} refuse={6:F3} tokens/case={7:F0}",
			report.CaseCount,
			report.RecallK,
			report.RecallAtKAverage,
			report.MeanReciprocalRank,
			report.NDcgAtKAverage,
			report.CitationCoverage,
			report.RefusalRate,
			report.TokensPerCase);
	}
}

/// <summary>Knobs for <see cref="HttpEvalBackend"/>.</summary>
public sealed class HttpEvalBackendOptions
{
	/// <summary>Base URL of the API (e.g. <c>https://api.example.com</c>). Required when <see cref="HttpClient.BaseAddress"/> is null.</summary>
	public string ApiBaseUrl { get; set; } = string.Empty;

	/// <summary>Bearer token for authenticated calls. Empty disables Authorization header (the API must be in unauth pilot mode).</summary>
	public string BearerToken { get; set; } = string.Empty;

	/// <summary>Top-k chunks to request from <c>/api/search/hybrid</c> and <c>/api/ask</c>. Default 10.</summary>
	public int RetrievalLimit { get; set; } = 10;

	/// <summary>Hybrid vector vs. text weight, 0..1. Default 0.6 matches the API default.</summary>
	public double HybridVectorWeight { get; set; } = 0.6;

	/// <summary>When false, the backend only calls retrieval. Default true.</summary>
	public bool UseAsk { get; set; } = true;

	/// <summary>
	/// Optional persona override threaded through both <c>/api/search/hybrid</c>
	/// and <c>/api/ask</c>. Null = use the bearer token's session
	/// persona (default). Non-null on hybrid search requires the
	/// bearer token to carry Admin role (the API enforces this); on
	/// <c>/api/ask</c> the API accepts the value unconditionally
	/// because persona is a per-call concern there.
	/// </summary>
	public string? PersonaId { get; set; }
}
