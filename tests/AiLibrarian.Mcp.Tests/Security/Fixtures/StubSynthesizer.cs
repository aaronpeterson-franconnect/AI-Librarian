using AiLibrarian.Security;

namespace AiLibrarian.Mcp.Tests.Security.Fixtures;

/// <summary>
/// Deterministic <see cref="IAskSynthesizer"/> stub. Captures the
/// synthesis request for assertions; returns a configurable string.
/// </summary>
internal sealed class StubSynthesizer : IAskSynthesizer
{
	private readonly Func<AskSynthesisRequest, string> _answer;

	public AskSynthesisRequest? LastRequest { get; private set; }

	public StubSynthesizer(string fixedAnswer)
	{
		_answer = _ => fixedAnswer;
	}

	public StubSynthesizer(Func<AskSynthesisRequest, string> answer)
	{
		_answer = answer;
	}

	public Task<string> SynthesizeAsync(AskSynthesisRequest request, CancellationToken cancellationToken)
	{
		LastRequest = request;
		return Task.FromResult(_answer(request));
	}

	public static StubSynthesizer Echo() => new(req => $"echo: {req.Query}");
}
