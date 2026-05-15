using System.Diagnostics.CodeAnalysis;

namespace AiLibrarian.Domain.Ingest;

/// <summary>Payload for Service Bus ingest queue messages (JSON body). Phase 1: blob location + optional hints for skill selection.</summary>
public sealed class IngestJobMessage
{
	/// <summary>HTTPS blob URL (e.g. Azure Blob) for the object to canonicalize.</summary>
	public string BlobUri { get; init; } = "";

	public string? CorrelationId { get; init; }

	/// <summary>MIME hint for <see cref="Skills.ISkillRegistry.ResolveByMimeType"/> when extension is ambiguous.</summary>
	public string? ContentType { get; init; }

	public string? OriginalFileName { get; init; }

	/// <summary>Optional catalog source row to attach chunks to in later slices.</summary>
	public Guid? SourceId { get; init; }

	public bool TryValidate([NotNullWhen(false)] out string? error)
	{
		if (string.IsNullOrWhiteSpace(BlobUri))
		{
			error = "BlobUri is required.";
			return false;
		}

		if (!Uri.TryCreate(BlobUri.Trim(), UriKind.Absolute, out var uri)
			|| (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
		{
			error = "BlobUri must be an absolute http or https URL.";
			return false;
		}

		error = null;
		return true;
	}
}
