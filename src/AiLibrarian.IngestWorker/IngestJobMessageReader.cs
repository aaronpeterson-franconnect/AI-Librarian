using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using AiLibrarian.Domain.Ingest;

namespace AiLibrarian.IngestWorker;

/// <summary>Deserializes <see cref="IngestJobMessage"/> from Service Bus message bodies (JSON, UTF-8).</summary>
internal static class IngestJobMessageReader
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	public static bool TryRead(BinaryData body, [NotNullWhen(true)] out IngestJobMessage? message, [NotNullWhen(false)] out string? error)
	{
		message = null;
		error = null;
		if (body.ToMemory().Length == 0)
		{
			error = "Message body is empty.";
			return false;
		}

		try
		{
			message = JsonSerializer.Deserialize<IngestJobMessage>(body, JsonOptions);
		}
		catch (JsonException ex)
		{
			error = ex.Message;
			return false;
		}

		if (message is null)
		{
			error = "Message deserialized to null.";
			return false;
		}

		if (!message.TryValidate(out var validationError))
		{
			error = validationError;
			return false;
		}

		return true;
	}
}
