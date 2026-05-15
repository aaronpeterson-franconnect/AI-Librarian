using AiLibrarian.Domain;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AiLibrarian.Eval.Loader;

/// <summary>
/// Reads <see cref="GoldenCase"/> records from the YAML files under
/// <c>tests/AiLibrarian.Eval/golden-sets/&lt;persona&gt;/*.yaml</c>.
/// Schema is the projection in <see cref="GoldenCaseDocument"/>;
/// snake-case keys ("expected_chunk_ids") map to the C# camel-case
/// properties via <see cref="UnderscoredNamingConvention"/>.
///
/// <para>
/// One file = one case. Loader returns cases in stable lexical order
/// of file name so a re-run of the same fixtures emits a stable
/// metric report (CI diffability).
/// </para>
/// </summary>
public static class GoldenCaseLoader
{
	private static readonly IDeserializer _yaml = new DeserializerBuilder()
		.WithNamingConvention(UnderscoredNamingConvention.Instance)
		.IgnoreUnmatchedProperties()
		.Build();

	/// <summary>Load every golden case under <paramref name="directory"/>.</summary>
	public static IReadOnlyList<GoldenCase> LoadAll(string directory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		if (!Directory.Exists(directory))
		{
			return Array.Empty<GoldenCase>();
		}

		var files = Directory
			.EnumerateFiles(directory, "*.yaml", SearchOption.AllDirectories)
			.OrderBy(p => p, StringComparer.Ordinal)
			.ToList();

		var cases = new List<GoldenCase>(files.Count);
		foreach (var file in files)
		{
			var raw = File.ReadAllText(file);
			GoldenCaseDocument doc;
			try
			{
				doc = _yaml.Deserialize<GoldenCaseDocument>(raw)
					?? throw new InvalidOperationException("Empty YAML document.");
			}
			catch (Exception ex)
			{
				throw new InvalidOperationException(
					$"Could not parse golden case '{file}': {ex.Message}",
					ex);
			}

			cases.Add(doc.ToGoldenCase(file));
		}

		return cases;
	}

	/// <summary>Load a single case by file path; useful for debugging an individual case.</summary>
	public static GoldenCase Load(string filePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		var raw = File.ReadAllText(filePath);
		var doc = _yaml.Deserialize<GoldenCaseDocument>(raw)
			?? throw new InvalidOperationException($"Empty YAML document at {filePath}.");
		return doc.ToGoldenCase(filePath);
	}
}

/// <summary>YAML projection of a single golden case file.</summary>
internal sealed class GoldenCaseDocument
{
	public string Id { get; set; } = string.Empty;
	public string Query { get; set; } = string.Empty;
	public string Persona { get; set; } = string.Empty;
	public string ClassificationScope { get; set; } = "Internal";
	public List<string> ExpectedChunkIds { get; set; } = [];
	public List<ExpectedCitationDocument> ExpectedCitations { get; set; } = [];
	public bool MustRefuse { get; set; }
	public Dictionary<string, string> Tags { get; set; } = new(StringComparer.Ordinal);

	public GoldenCase ToGoldenCase(string sourceFile)
	{
		var id = string.IsNullOrWhiteSpace(Id) ? Path.GetFileNameWithoutExtension(sourceFile) : Id;
		var classification = Enum.TryParse<Classification>(ClassificationScope, ignoreCase: true, out var parsed)
			? parsed
			: Classification.Internal;

		var chunkIds = new List<Guid>(ExpectedChunkIds.Count);
		foreach (var raw in ExpectedChunkIds)
		{
			if (Guid.TryParse(raw, out var g))
			{
				chunkIds.Add(g);
			}
		}

		var citations = new List<ExpectedCitation>(ExpectedCitations.Count);
		foreach (var c in ExpectedCitations)
		{
			if (Guid.TryParse(c.SourceId, out var sid))
			{
				citations.Add(new ExpectedCitation(sid, c.SpanAnchor ?? string.Empty, c.MinConfidence));
			}
		}

		return new GoldenCase(
			Id: id,
			Query: Query,
			Persona: string.IsNullOrWhiteSpace(Persona) ? "engineering" : Persona,
			ClassificationScope: classification,
			ExpectedChunkIds: chunkIds,
			ExpectedCitations: citations,
			MustRefuse: MustRefuse,
			Tags: Tags);
	}
}

/// <summary>YAML projection of a single expected citation row.</summary>
internal sealed class ExpectedCitationDocument
{
	public string SourceId { get; set; } = string.Empty;
	public string? SpanAnchor { get; set; }
	public double MinConfidence { get; set; } = 0.7;
}
