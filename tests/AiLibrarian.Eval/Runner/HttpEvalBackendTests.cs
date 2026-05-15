using System.Net;
using System.Text;
using System.Text.Json;

using AiLibrarian.Domain;

namespace AiLibrarian.Eval.Runner;

/// <summary>
/// Pins the HTTP backend's contract with the API. Doesn't stand up a
/// real API — uses a tiny <see cref="HttpMessageHandler"/> stub so the
/// tests run in milliseconds and don't depend on either Postgres or
/// the LLM gateway.
/// </summary>
public sealed class HttpEvalBackendTests
{
	private static readonly Guid ChunkA = Guid.Parse("11111111-1111-1111-1111-111111111111");
	private static readonly Guid ChunkB = Guid.Parse("22222222-2222-2222-2222-222222222222");
	private static readonly Guid ChunkC = Guid.Parse("33333333-3333-3333-3333-333333333333");

	[Fact]
	public async Task Retrieval_Only_Mode_Skips_Ask()
	{
		var handler = new RecordingHandler
		{
			Responder = (req, ct) =>
			{
				if (req.RequestUri!.AbsolutePath.EndsWith("/api/search/hybrid", StringComparison.Ordinal))
				{
					return JsonResponse(BuildHybridResponseJson(ChunkA, ChunkB));
				}

				throw new InvalidOperationException($"Unexpected request to {req.RequestUri}");
			},
		};
		using var http = new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") };
		var backend = new HttpEvalBackend(http, new HttpEvalBackendOptions { UseAsk = false });

		var outcome = await backend.RunOneAsync(MakeCase(), CancellationToken.None);

		outcome.RetrievedChunkIds.Should().BeEquivalentTo(new[] { ChunkA, ChunkB });
		outcome.ClaimCount.Should().Be(0);
		outcome.CitedClaimCount.Should().Be(0);
		outcome.Refused.Should().BeFalse();
		handler.Recorded.Should().HaveCount(1, "retrieval-only mode skips /api/ask");
	}

	[Fact]
	public async Task Full_Mode_Calls_Retrieval_Then_Ask()
	{
		var handler = new RecordingHandler
		{
			Responder = (req, ct) =>
			{
				if (req.RequestUri!.AbsolutePath.EndsWith("/api/search/hybrid", StringComparison.Ordinal))
				{
					return JsonResponse(BuildHybridResponseJson(ChunkA, ChunkB, ChunkC));
				}

				if (req.RequestUri.AbsolutePath.EndsWith("/api/ask", StringComparison.Ordinal))
				{
					return JsonResponse(BuildAskResponseJson(admitted: true, answer: "synthesis", chunkIds: new[] { ChunkA, ChunkB }));
				}

				throw new InvalidOperationException($"Unexpected request to {req.RequestUri}");
			},
		};
		using var http = new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") };
		var backend = new HttpEvalBackend(http, new HttpEvalBackendOptions { UseAsk = true });

		var outcome = await backend.RunOneAsync(MakeCase(), CancellationToken.None);

		outcome.RetrievedChunkIds.Should().HaveCount(3);
		outcome.ClaimCount.Should().Be(1);
		outcome.CitedClaimCount.Should().Be(1, "admitted answer with non-empty chunk ids counts as cited");
		outcome.Refused.Should().BeFalse();
		handler.Recorded.Should().HaveCount(2);
	}

	[Fact]
	public async Task Ask_Refusal_Payload_Counts_As_Refused()
	{
		var handler = new RecordingHandler
		{
			Responder = (req, ct) =>
			{
				if (req.RequestUri!.AbsolutePath.EndsWith("/api/search/hybrid", StringComparison.Ordinal))
				{
					return JsonResponse(BuildHybridResponseJson(ChunkA));
				}

				return JsonResponse(BuildAskResponseJson(
					admitted: false,
					answer: null,
					chunkIds: Array.Empty<Guid>(),
					refusalReason: "NoSources",
					refusalDetail: "no source material"));
			},
		};
		using var http = new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") };
		var backend = new HttpEvalBackend(http, new HttpEvalBackendOptions { UseAsk = true });

		var outcome = await backend.RunOneAsync(MakeCase(mustRefuse: true), CancellationToken.None);

		outcome.Refused.Should().BeTrue();
		outcome.ClaimCount.Should().Be(0);
		outcome.CitedClaimCount.Should().Be(0);
	}

	[Fact]
	public async Task Rate_Limit_429_Maps_To_Refused()
	{
		var handler = new RecordingHandler
		{
			Responder = (req, ct) =>
			{
				if (req.RequestUri!.AbsolutePath.EndsWith("/api/search/hybrid", StringComparison.Ordinal))
				{
					return JsonResponse(BuildHybridResponseJson(ChunkA));
				}

				return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
				{
					Content = new StringContent("{\"refusal\":{\"reason\":\"RateLimited\"}}", Encoding.UTF8, "application/json"),
				};
			},
		};
		using var http = new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") };
		var backend = new HttpEvalBackend(http, new HttpEvalBackendOptions { UseAsk = true });

		var outcome = await backend.RunOneAsync(MakeCase(), CancellationToken.None);

		outcome.Refused.Should().BeTrue();
	}

	[Fact]
	public async Task Retrieval_500_Throws_With_Case_Context()
	{
		var handler = new RecordingHandler
		{
			Responder = (req, ct) => new HttpResponseMessage(HttpStatusCode.InternalServerError)
			{
				Content = new StringContent("backend died"),
			},
		};
		using var http = new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") };
		var backend = new HttpEvalBackend(http, new HttpEvalBackendOptions { UseAsk = false });

		var act = () => backend.RunOneAsync(MakeCase(id: "eng-007"), CancellationToken.None);

		(await act.Should().ThrowAsync<InvalidOperationException>())
			.Which.Message.Should().Contain("case eng-007");
	}

	[Fact]
	public async Task Bearer_Token_When_Set_Is_Attached_To_Both_Calls()
	{
		var handler = new RecordingHandler
		{
			Responder = (req, ct) =>
			{
				if (req.RequestUri!.AbsolutePath.EndsWith("/api/search/hybrid", StringComparison.Ordinal))
				{
					return JsonResponse(BuildHybridResponseJson(ChunkA));
				}

				return JsonResponse(BuildAskResponseJson(admitted: true, answer: "x", chunkIds: new[] { ChunkA }));
			},
		};
		using var http = new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") };
		var backend = new HttpEvalBackend(http, new HttpEvalBackendOptions
		{
			BearerToken = "test-token-abc",
			UseAsk = true,
		});

		await backend.RunOneAsync(MakeCase(), CancellationToken.None);

		handler.Recorded.Should().HaveCount(2);
		handler.Recorded.Should().AllSatisfy(req =>
		{
			req.Headers.Authorization.Should().NotBeNull();
			req.Headers.Authorization!.Scheme.Should().Be("Bearer");
			req.Headers.Authorization.Parameter.Should().Be("test-token-abc");
		});
	}

	[Fact]
	public async Task Persona_id_is_threaded_into_both_retrieval_and_ask_request_bodies()
	{
		var persona = "99999999-1111-1111-1111-111111111111";
		var capturedBodies = new List<string>();

		var handler = new RecordingHandler
		{
			Responder = (req, ct) =>
			{
				// Read the request body synchronously off the test handler.
				// HttpRequestMessage.Content is still readable here because
				// we haven't disposed the request.
				var body = req.Content?.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult() ?? string.Empty;
				capturedBodies.Add(body);

				if (req.RequestUri!.AbsolutePath.EndsWith("/api/search/hybrid", StringComparison.Ordinal))
				{
					return JsonResponse(BuildHybridResponseJson(ChunkA));
				}
				if (req.RequestUri.AbsolutePath.EndsWith("/api/ask", StringComparison.Ordinal))
				{
					return JsonResponse(BuildAskResponseJson(admitted: true, answer: "x", chunkIds: new[] { ChunkA }));
				}
				throw new InvalidOperationException(req.RequestUri.ToString());
			},
		};
		using var http = new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") };
		var backend = new HttpEvalBackend(http, new HttpEvalBackendOptions
		{
			UseAsk = true,
			PersonaId = persona,
		});

		await backend.RunOneAsync(MakeCase(), CancellationToken.None);

		capturedBodies.Should().HaveCount(2,
			"the backend sends one body per endpoint (retrieval + ask)");
		capturedBodies.Should().AllSatisfy(body =>
		{
			body.Should().Contain(persona,
				"both /api/search/hybrid and /api/ask payloads must carry the configured PersonaId");
		});
	}

	[Fact]
	public async Task Persona_id_is_omitted_when_options_PersonaId_is_null()
	{
		string? hybridBody = null;
		var handler = new RecordingHandler
		{
			Responder = (req, ct) =>
			{
				if (req.RequestUri!.AbsolutePath.EndsWith("/api/search/hybrid", StringComparison.Ordinal))
				{
					hybridBody = req.Content?.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
					return JsonResponse(BuildHybridResponseJson(ChunkA));
				}
				throw new InvalidOperationException(req.RequestUri.ToString());
			},
		};
		using var http = new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") };
		var backend = new HttpEvalBackend(http, new HttpEvalBackendOptions { UseAsk = false, PersonaId = null });

		await backend.RunOneAsync(MakeCase(), CancellationToken.None);

		hybridBody.Should().NotBeNull();
		// JsonIgnoreCondition.WhenWritingNull is set on the writer
		// options, so a null PersonaId stays out of the serialised body
		// entirely.
		hybridBody!.Should().NotContain("personaId",
			"a null PersonaId must NOT serialise as 'personaId: null' -- the API treats absence as 'use session persona'");
	}

	[Fact]
	public void FormatReportSummary_Produces_Stable_String()
	{
		var report = new EvalReport(
			CaseCount: 50,
			RecallAtKAverage: 0.875,
			MeanReciprocalRank: 0.7,
			NDcgAtKAverage: 0.812,
			CitationCoverage: 0.97,
			RefusalRate: 0.92,
			TokensPerCase: 1200.5,
			RecallK: 10,
			Cases: Array.Empty<EvalCaseResult>());

		var summary = HttpEvalBackend.FormatReportSummary(report);

		summary.Should().Contain("recall@10=0.875");
		summary.Should().Contain("cov=0.970");
		summary.Should().Contain("refuse=0.920");
	}

	private static GoldenCase MakeCase(string id = "case-1", bool mustRefuse = false) => new(
		Id: id,
		Query: "how do I deploy the worker?",
		Persona: "engineering",
		ClassificationScope: Classification.Internal,
		ExpectedChunkIds: new[] { ChunkA, ChunkB },
		ExpectedCitations: Array.Empty<ExpectedCitation>(),
		MustRefuse: mustRefuse,
		Tags: new Dictionary<string, string>(StringComparer.Ordinal));

	private static HttpResponseMessage JsonResponse(string body)
		=> new(HttpStatusCode.OK)
		{
			Content = new StringContent(body, Encoding.UTF8, "application/json"),
		};

	private static string BuildHybridResponseJson(params Guid[] chunkIds)
	{
		var hits = chunkIds.Select((id, i) => new
		{
			chunkId = id,
			sourceId = Guid.NewGuid(),
			orderIndex = i,
			excerpt = "stub",
			hybridScore = 1.0 - (i * 0.1),
			cosineDistance = (double?)0.1,
			textRank = (double?)0.5,
			sourceClassification = "Internal",
			sourceDepartmentId = (Guid?)null,
		});
		return JsonSerializer.Serialize(new { correlationId = Guid.NewGuid(), embeddingDeployment = "stub", hits });
	}

	private static string BuildAskResponseJson(
		bool admitted,
		string? answer,
		IReadOnlyList<Guid> chunkIds,
		string? refusalReason = null,
		string? refusalDetail = null)
	{
		return JsonSerializer.Serialize(new
		{
			correlationId = Guid.NewGuid(),
			admitted,
			answer,
			refusal = refusalReason is null ? null : new { reason = refusalReason, detail = refusalDetail },
			chunkIds,
			redactionCandidates = 0,
			redactionMode = "Shadow",
		});
	}

	private sealed class RecordingHandler : HttpMessageHandler
	{
		public List<HttpRequestMessage> Recorded { get; } = new();
		public Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> Responder { get; set; } =
			(_, _) => new HttpResponseMessage(HttpStatusCode.NotImplemented);

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Recorded.Add(request);
			return Task.FromResult(Responder(request, cancellationToken));
		}
	}
}
