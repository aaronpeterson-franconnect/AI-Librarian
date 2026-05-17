// SKEXP0001 + SKEXP0010 are Semantic Kernel's "evaluation API" diagnostics
// for the embedding-generation service surface. The production
// LlmKernelFactory suppresses them via the project-wide NoWarn list;
// this test file is in a tests project without that suppression, so
// we silence them locally. Reviewing this in 6 months: if Microsoft
// graduates these APIs out of experimental, drop the pragma.
#pragma warning disable SKEXP0001, SKEXP0010, CA1861, IDE0011

using System.Net;
using System.Net.Http;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Embeddings;

namespace AiLibrarian.LlmGateway.Tests;

/// <summary>
/// Validates that AiLibrarian.LlmMock's response shape is consumable by
/// the Semantic-Kernel-built OpenAI client. The previous mock PR
/// (Phase 2B v1) shipped a manually-usable mock but the CI gate was
/// deferred because seven CI iterations couldn't pin down a
/// "FormatException: input is not a valid Base64 string of encoded
/// floats" thrown by the OpenAI SDK against the mock's JSON output.
///
/// <para>Lesson learned the expensive way: validating response-shape
/// compatibility against the actual SDK belongs in a unit test, not
/// in 5-minute CI iterations. This test stands a stub HttpMessageHandler
/// in front of the SK-built kernel and feeds it the EXACT JSON the
/// mock produces. If the SDK can parse that, the live mock will
/// also work. If the SDK can't, the test fails fast and we iterate
/// without burning CI minutes.</para>
/// </summary>
public sealed class MockResponseRoundtripTests
{
	[Fact]
	public async Task SK_OpenAI_client_parses_base64_response_when_encoding_format_is_base64()
	{
		// Build the mock's response with a single 1536-float embedding,
		// encoded as base64 little-endian -- the exact shape Program.cs
		// produces when encoding_format=base64 is in the request.
		var vec = DeterministicEmbedding("test query", 1536);
		var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(vec.AsSpan()).ToArray();
		var base64 = Convert.ToBase64String(bytes);
		var responseBody = BuildEmbeddingsResponse(base64Embeddings: new[] { base64 });

		// Plug a stub HttpMessageHandler into the SK kernel via an
		// HttpClient. The SK OpenAI client will call /embeddings on
		// this client; the handler returns our canned response.
		using var handler = new StubHandler(responseBody);
		using var httpClient = new HttpClient(handler)
		{
			// MUST end in /v1/ so SK's relative `embeddings` resolves
			// to /v1/embeddings. Same trick LlmKernelFactory uses for
			// the live mock.
			BaseAddress = new Uri("http://stub.local/v1/"),
		};

		var kb = Kernel.CreateBuilder();
		kb.Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
		kb.AddOpenAITextEmbeddingGeneration(
			modelId: "text-embedding-3-large",
			apiKey: "mock-key",
			httpClient: httpClient,
			serviceId: "test-embed");
		var kernel = kb.Build();

		var embedder = kernel.GetRequiredService<ITextEmbeddingGenerationService>();

		// The act under test: if my mock's JSON shape is parseable by
		// the SDK, this call succeeds and returns a 1536-dim vector.
		// If not, it throws FormatException with "input is not a valid
		// Base64 string of encoded floats" -- the same error CI saw.
		var result = await embedder.GenerateEmbeddingsAsync(new[] { "test query" });

		result.Should().HaveCount(1,
			because: "we asked for one embedding and the response has one data entry.");
		result[0].Length.Should().Be(1536,
			because: "we encoded a 1536-dim vector, the SDK should decode it round-trip.");
	}

	[Fact]
	public async Task Base64_with_JSON_unicode_escapes_fails_to_parse()
	{
		// Reproducer for the prior CI failure. STJ's default
		// JavaScriptEncoder escapes `+` to `+` and `/` to `\/`
		// in output JSON, even though both characters are legal in
		// JSON string literals. The OpenAI SDK appears to base64-
		// decode the embedding field's RAW JSON bytes (not the
		// decoded string), so `+` becomes a literal 6-byte
		// sequence that breaks base64 parsing.
		var vec = DeterministicEmbedding("test query", 1536);
		var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(vec.AsSpan()).ToArray();
		var base64 = Convert.ToBase64String(bytes);
		// Force the unicode-escape variant. Real STJ would emit this
		// shape unless we configure UnsafeRelaxedJsonEscaping.
		var escapedBase64 = base64.Replace("+", "\\u002B").Replace("/", "\\u002F");
		var responseBody = BuildEmbeddingsResponse(base64Embeddings: new[] { escapedBase64 });

		using var handler = new StubHandler(responseBody);
		using var httpClient = new HttpClient(handler)
		{
			BaseAddress = new Uri("http://stub.local/v1/"),
		};

		var kb = Kernel.CreateBuilder();
		kb.Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
		kb.AddOpenAITextEmbeddingGeneration(
			modelId: "text-embedding-3-large",
			apiKey: "mock-key",
			httpClient: httpClient,
			serviceId: "test-embed");
		var kernel = kb.Build();

		var embedder = kernel.GetRequiredService<ITextEmbeddingGenerationService>();

		// Expecting the same FormatException CI saw. If this test
		// passes (no exception), the hypothesis is wrong and we need
		// to keep looking.
		var act = async () => await embedder.GenerateEmbeddingsAsync(new[] { "test query" });
		await act.Should().ThrowAsync<FormatException>(
			because: "STJ-escaped base64 (`\\u002B` instead of `+`) is not parseable by " +
				"the OpenAI SDK's base64 decoder. This is the bug the live mock hit in CI.");
	}

	[Fact]
	public async Task OpenAI_SDK_rejects_float_array_responses_so_mock_must_always_return_base64()
	{
		// Surprising finding: the OpenAI .NET SDK does NOT handle the
		// JSON-array form of the `embedding` field, even though the
		// OpenAI HTTP API spec supports both encoding_format=float
		// (array) and encoding_format=base64 (string). The Python and
		// JS SDKs handle both; .NET hard-codes base64. This test
		// documents that constraint so a future refactor doesn't
		// "improve" the mock to send arrays when no encoding_format
		// is specified -- the .NET-driven api callers would break.
		var vec = DeterministicEmbedding("test query", 1536);
		var responseBody = BuildEmbeddingsResponse(floatEmbeddings: new[] { vec });

		using var handler = new StubHandler(responseBody);
		using var httpClient = new HttpClient(handler)
		{
			BaseAddress = new Uri("http://stub.local/v1/"),
		};

		var kb = Kernel.CreateBuilder();
		kb.Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
		kb.AddOpenAITextEmbeddingGeneration(
			modelId: "text-embedding-3-large",
			apiKey: "mock-key",
			httpClient: httpClient,
			serviceId: "test-embed");
		var kernel = kb.Build();

		var embedder = kernel.GetRequiredService<ITextEmbeddingGenerationService>();

		var act = async () => await embedder.GenerateEmbeddingsAsync(new[] { "test query" });
		await act.Should().ThrowAsync<FormatException>(
			because: "the OpenAI .NET SDK hard-codes base64 decoding; a JSON " +
				"array of floats fails Convert.TryFromBase64String on the raw bytes.");
	}

	// ---- helpers ----

	private static float[] DeterministicEmbedding(string input, int dims)
	{
		// Same hash-seeded RNG the mock uses; kept aligned so a future
		// "is the mock's vector for input X equal to test's vector"
		// assertion would also work.
		var seedBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input));
		var seed = BitConverter.ToInt32(seedBytes, 0);
		var rng = new Random(seed);
		var vec = new float[dims];
		for (var i = 0; i < dims; i++)
		{
			vec[i] = (float)((rng.NextDouble() * 2.0) - 1.0);
		}
		return vec;
	}

	private static string BuildEmbeddingsResponse(
		string[]? base64Embeddings = null,
		float[][]? floatEmbeddings = null)
	{
		// Mirrors the mock's response shape. Writing JSON by hand
		// (rather than serializing an object) lets us control the
		// EXACT wire bytes the SDK will see -- the failure we're
		// trying to diagnose hinges on serialization details, so the
		// test mustn't go through the same serializer.
		var sb = new StringBuilder();
		sb.Append("{\"object\":\"list\",\"data\":[");
		var count = base64Embeddings?.Length ?? floatEmbeddings?.Length ?? 0;
		for (var i = 0; i < count; i++)
		{
			if (i > 0) sb.Append(',');
			sb.Append("{\"object\":\"embedding\",\"embedding\":");
			if (base64Embeddings is not null)
			{
				sb.Append('"').Append(base64Embeddings[i]).Append('"');
			}
			else
			{
				sb.Append('[');
				var vec = floatEmbeddings![i];
				for (var j = 0; j < vec.Length; j++)
				{
					if (j > 0) sb.Append(',');
					// Round-trip safe float format: 'R' or 'G9' in
					// invariant culture. The mock uses STJ which picks
					// G9-ish. Either parses fine.
					sb.Append(vec[j].ToString("G9", System.Globalization.CultureInfo.InvariantCulture));
				}
				sb.Append(']');
			}
			sb.Append(",\"index\":").Append(i).Append('}');
		}
		sb.Append("],\"model\":\"text-embedding-3-large\",\"usage\":{\"prompt_tokens\":3,\"total_tokens\":3}}");
		return sb.ToString();
	}

	private sealed class StubHandler : HttpMessageHandler
	{
		private readonly string _body;

		public StubHandler(string body)
		{
			_body = body;
		}

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(_body, Encoding.UTF8, "application/json"),
			});
		}
	}
}
