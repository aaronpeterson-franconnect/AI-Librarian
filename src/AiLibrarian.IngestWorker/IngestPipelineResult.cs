namespace AiLibrarian.IngestWorker;

internal readonly record struct IngestPipelineResult(bool Ok, string? DeadLetterReason, string? DeadLetterDescription)
{
	public static IngestPipelineResult Completed() => new(true, null, null);

	public static IngestPipelineResult DeadLetter(string reason, string description) =>
		new(false, reason, description);
}
