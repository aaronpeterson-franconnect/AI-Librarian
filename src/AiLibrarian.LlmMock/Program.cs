using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// Minimal ASP.NET Core API that imitates Azure OpenAI's embeddings
// endpoint with deterministic hash-seeded vectors. Same input string
// -> same vector across runs, which is the property that lets the eval
// harness produce reproducible retrieval rankings without paying for
// real LLM calls.
//
// What this DOES:
// - POST /openai/deployments/{deployment}/embeddings  (any query string,
//   any api-key header, accepts both string and array `input`)
// - Returns the Azure OpenAI v1 response shape: object/list/data/usage,
//   with `embedding` as a 1536-dim float[] hashed deterministically off
//   the input string.
// - Logs each call so the docker-compose log shows traffic for the
//   debugging that inevitably follows.
//
// What this does NOT do:
// - Honor the `api-key` header. Auth is unused.
// - Reproduce semantic similarity. Two unrelated strings produce
//   uncorrelated vectors; two related strings ALSO produce uncorrelated
//   vectors. This mock is for plumbing gating only.
// - Implement /chat/completions (the chat endpoint). /api/ask still
//   returns its no-LLM error when this mock is active; the gap is
//   tracked in the runbook.

const int VectorDimensions = 1536;   // text-embedding-3-large default
const string DefaultModel = "text-embedding-3-large";

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(opt => opt.SingleLine = true);

var app = builder.Build();

// Health probe -- the compose healthcheck on the llm-mock service uses
// this so dependent services (the api) can wait on a `service_healthy`
// gate before they start.
app.MapGet("/health", () => Results.Ok(new { status = "ok", mock = "azure-openai-embeddings" }));

// Embeddings. The route catches any deployment name; the SK-built URL
// is `{endpoint}/openai/deployments/{deployment}/embeddings?api-version=...`
// and we don't care which deployment name the caller picked -- we just
// hash whatever input arrives.
app.MapPost("/openai/deployments/{deployment}/embeddings",
	async (string deployment, HttpRequest req, ILogger<Program> logger) =>
{
	using var doc = await JsonDocument.ParseAsync(req.Body).ConfigureAwait(false);
	var root = doc.RootElement;

	// `input` may be a single string or an array. The Azure SDK uses
	// arrays for batch embeds; older clients sometimes send a bare
	// string. Normalize to a string[].
	var inputs = root.TryGetProperty("input", out var inputElem)
		? ExtractInputs(inputElem)
		: Array.Empty<string>();

	if (inputs.Length == 0)
	{
		logger.LogWarning("Embeddings request had no `input` array; returning 400.");
		return Results.BadRequest(new { error = "`input` field is required (string or string[])." });
	}

	var data = new List<EmbeddingData>(inputs.Length);
	var promptTokens = 0;
	for (var i = 0; i < inputs.Length; i++)
	{
		var vec = DeterministicEmbedding(inputs[i], VectorDimensions);
		data.Add(new EmbeddingData(Object: "embedding", Embedding: vec, Index: i));
		// Trivial token estimate, matching the API's own approximation
		// (chars/4, at least 1). Keeps the usage figure plausible without
		// pulling in a tokenizer.
		promptTokens += Math.Max(1, inputs[i].Length / 4);
	}

	var response = new EmbeddingsResponse(
		Object: "list",
		Data: data,
		Model: DefaultModel,
		Usage: new EmbeddingsUsage(PromptTokens: promptTokens, TotalTokens: promptTokens));

	logger.LogInformation(
		"embeddings deployment={Deployment} inputs={Count} dims={Dims}",
		deployment, inputs.Length, VectorDimensions);

	return Results.Json(response, MockJsonOptions.Default);
});

app.MapFallback((HttpContext ctx, ILogger<Program> logger) =>
{
	// Anything we haven't implemented yet (chat/completions, models, etc.)
	// surfaces as a clear "mock doesn't handle this" so the operator
	// knows to either extend the mock or wire real Azure OpenAI.
	logger.LogWarning(
		"Unhandled mock request {Method} {Path}; the mock only implements /openai/deployments/{{deployment}}/embeddings today.",
		ctx.Request.Method, ctx.Request.Path);
	return Results.StatusCode(StatusCodes.Status501NotImplemented);
});

app.Run();

// --- helpers ---

static string[] ExtractInputs(JsonElement inputElem)
{
	if (inputElem.ValueKind == JsonValueKind.String)
	{
		return new[] { inputElem.GetString() ?? string.Empty };
	}

	if (inputElem.ValueKind == JsonValueKind.Array)
	{
		var items = new List<string>(inputElem.GetArrayLength());
		foreach (var e in inputElem.EnumerateArray())
		{
			items.Add(e.GetString() ?? string.Empty);
		}
		return items.ToArray();
	}

	return Array.Empty<string>();
}

static float[] DeterministicEmbedding(string input, int dims)
{
	// SHA256 of the input seeds a small RNG that walks the full vector.
	// Two passes (256 bits = 32 bytes, but we need ~6KB for 1536 floats)
	// would normally use a stream cipher; here we just rehash with a
	// counter for each chunk. Same input -> same RNG state -> same vector.
	var seedBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
	var seed = BitConverter.ToInt32(seedBytes, 0);
	var rng = new Random(seed);

	var vec = new float[dims];
	for (var i = 0; i < dims; i++)
	{
		// Uniform [-1.0, 1.0]; the API's pgvector cosine-similarity
		// downstream doesn't care about the distribution as long as it's
		// consistent. Real embeddings are unit-normalized, but for
		// plumbing-gate purposes the un-normalized form works.
		vec[i] = (float)((rng.NextDouble() * 2.0) - 1.0);
	}
	return vec;
}

// JSON shapes -- camelCase to match Azure OpenAI's REST response.
//
// Note: a `static readonly` field can't live at file scope in a
// top-level program, so the options are held in a small static class.
internal static class MockJsonOptions
{
	public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};
}

internal sealed record EmbeddingsResponse(
	[property: JsonPropertyName("object")] string Object,
	[property: JsonPropertyName("data")] IReadOnlyList<EmbeddingData> Data,
	[property: JsonPropertyName("model")] string Model,
	[property: JsonPropertyName("usage")] EmbeddingsUsage Usage);

internal sealed record EmbeddingData(
	[property: JsonPropertyName("object")] string Object,
	[property: JsonPropertyName("embedding")] float[] Embedding,
	[property: JsonPropertyName("index")] int Index);

internal sealed record EmbeddingsUsage(
	[property: JsonPropertyName("prompt_tokens")] int PromptTokens,
	[property: JsonPropertyName("total_tokens")] int TotalTokens);
