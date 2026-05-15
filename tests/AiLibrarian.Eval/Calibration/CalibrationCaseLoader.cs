using AiLibrarian.Domain.Citations;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AiLibrarian.Eval.Calibration;

/// <summary>
/// Reads <see cref="CalibrationCase"/> records from YAML files under
/// <c>tests/AiLibrarian.Eval/golden-sets/calibration/*.yaml</c>. Mirrors
/// <see cref="Loader.GoldenCaseLoader"/>: snake-case keys, one file per
/// case, stable lexical ordering for CI diffability.
/// </summary>
public static class CalibrationCaseLoader
{
	private static readonly IDeserializer _yaml = new DeserializerBuilder()
		.WithNamingConvention(UnderscoredNamingConvention.Instance)
		.IgnoreUnmatchedProperties()
		.Build();

	/// <summary>Load every calibration case under <paramref name="directory"/>.</summary>
	public static IReadOnlyList<CalibrationCase> LoadAll(string directory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		if (!Directory.Exists(directory))
		{
			return Array.Empty<CalibrationCase>();
		}

		var files = Directory
			.EnumerateFiles(directory, "*.yaml", SearchOption.AllDirectories)
			.OrderBy(p => p, StringComparer.Ordinal)
			.ToList();

		var cases = new List<CalibrationCase>(files.Count);
		foreach (var file in files)
		{
			cases.Add(LoadInternal(file));
		}

		return cases;
	}

	/// <summary>Load a single calibration case by path.</summary>
	public static CalibrationCase Load(string filePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		return LoadInternal(filePath);
	}

	private static CalibrationCase LoadInternal(string file)
	{
		var raw = File.ReadAllText(file);
		CalibrationCaseDocument doc;
		try
		{
			doc = _yaml.Deserialize<CalibrationCaseDocument>(raw)
				?? throw new InvalidOperationException("Empty YAML document.");
		}
		catch (Exception ex)
		{
			throw new InvalidOperationException(
				$"Could not parse calibration case '{file}': {ex.Message}",
				ex);
		}

		return doc.ToCase(file);
	}
}

/// <summary>YAML projection of a single calibration-case file.</summary>
internal sealed class CalibrationCaseDocument
{
	public string Id { get; set; } = string.Empty;
	public string ClaimText { get; set; } = string.Empty;
	public List<CalibrationChunkDocument> CitedChunks { get; set; } = [];
	public string HumanVerdict { get; set; } = "Unverifiable";
	public double HumanConfidence { get; set; } = 1.0;
	public string HumanRationale { get; set; } = string.Empty;
	public Dictionary<string, string> Tags { get; set; } = new(StringComparer.Ordinal);

	public CalibrationCase ToCase(string sourceFile)
	{
		var id = string.IsNullOrWhiteSpace(Id) ? Path.GetFileNameWithoutExtension(sourceFile) : Id;

		if (!Enum.TryParse<ClaimVerdict>(HumanVerdict, ignoreCase: true, out var verdict))
		{
			throw new InvalidOperationException(
				$"Calibration case '{sourceFile}' has unknown human_verdict '{HumanVerdict}'. "
				+ "Must be Supported, NotSupported, Partial, or Unverifiable.");
		}

		var chunks = new List<CalibrationChunk>(CitedChunks.Count);
		foreach (var c in CitedChunks)
		{
			if (!Guid.TryParse(c.ChunkId, out var cid))
			{
				throw new InvalidOperationException(
					$"Calibration case '{sourceFile}' chunk_id is not a GUID: '{c.ChunkId}'.");
			}
			chunks.Add(new CalibrationChunk(cid, c.Text ?? string.Empty));
		}

		var confidence = Math.Clamp(HumanConfidence, 0.0, 1.0);

		return new CalibrationCase(
			Id: id,
			ClaimText: ClaimText ?? string.Empty,
			CitedChunks: chunks,
			HumanVerdict: verdict,
			HumanConfidence: confidence,
			HumanRationale: HumanRationale ?? string.Empty,
			Tags: Tags);
	}
}

/// <summary>YAML projection of one cited chunk inside a calibration case.</summary>
internal sealed class CalibrationChunkDocument
{
	public string ChunkId { get; set; } = string.Empty;
	public string? Text { get; set; }
}
