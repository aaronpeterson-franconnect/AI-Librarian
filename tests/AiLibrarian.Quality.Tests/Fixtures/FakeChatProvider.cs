using System.Runtime.CompilerServices;

using AiLibrarian.LlmGateway.Abstractions;

namespace AiLibrarian.Quality.Tests.Fixtures;

/// <summary>
/// Deterministic <see cref="IChatProvider"/> that returns whatever
/// string the test stages. Lets us exercise the grader's parser path
/// without standing up a real Azure OpenAI deployment.
/// </summary>
internal sealed class FakeChatProvider : IChatProvider
{
	private string _response = string.Empty;

	public string ProviderId => "fake";

	public FakeChatProvider Returning(string response)
	{
		_response = response;
		return this;
	}

	public async IAsyncEnumerable<ChatCompletionChunk> StreamCompletionAsync(
		ChatCompletionRequest request,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		// Yield once to keep the contract honest (real providers stream).
		await Task.Yield();
		yield return new ChatCompletionChunk(_response, FinishReason: "stop");
	}
}
