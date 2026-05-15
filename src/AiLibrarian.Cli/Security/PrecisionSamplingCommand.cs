using System.CommandLine;
using System.Globalization;
using System.Text;

using AiLibrarian.Security;

namespace AiLibrarian.Cli.Security;

/// <summary>
/// `ailib precision-sampling` — computes per-kind precision + the
/// enforce-readiness verdict for the secret redactor (ADR 0017).
/// Input is a CSV the operator labeled by hand after exporting
/// shadow-mode candidates from the audit ledger.
///
/// <para>CSV columns (header required):</para>
/// <code>
///   kind,is_true_positive
///   jwt,true
///   aws_access_key,true
///   api_key_assignment,false
/// </code>
///
/// <para>Lines beginning with <c>#</c> are treated as comments.
/// Empty lines are skipped.</para>
/// </summary>
public static class PrecisionSamplingCommand
{
	private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
	};

	public static Command Build()
	{
		var labelsOption = new Option<FileInfo>("--labels")
		{
			IsRequired = true,
			Description = "Path to the labeled CSV (kind,is_true_positive).",
		};

		var floorOption = new Option<double>("--floor", () => 0.9)
		{
			Description = "Per-kind precision floor required for enforce-ready (default 0.9 per ADR 0017).",
		};

		var minSampleOption = new Option<int>("--min-sample", () => 100)
		{
			Description = "Minimum total labeled samples for a defensible verdict (default 100).",
		};

		var jsonOption = new Option<bool>("--json")
		{
			Description = "Emit the report as JSON instead of human-readable text.",
		};

		var cmd = new Command("precision-sampling", "Compute per-kind precision for the secret redactor (ADR 0017 enforce-mode gate).")
		{
			labelsOption,
			floorOption,
			minSampleOption,
			jsonOption,
		};

		cmd.SetHandler((FileInfo labelsFile, double floor, int minSample, bool json) =>
		{
			if (!labelsFile.Exists)
			{
				Console.Error.WriteLine($"Labels file not found: {labelsFile.FullName}");
				Environment.Exit(2);
				return;
			}

			IReadOnlyList<LabeledCandidate> labels;
			try
			{
				labels = LoadLabels(labelsFile);
			}
			catch (FormatException ex)
			{
				Console.Error.WriteLine($"CSV parse error: {ex.Message}");
				Environment.Exit(3);
				return;
			}

			PrecisionReport report;
			try
			{
				report = PrecisionSampling.Compute(labels, precisionFloor: floor, minSampleSize: minSample);
			}
			catch (ArgumentOutOfRangeException ex)
			{
				Console.Error.WriteLine($"Invalid argument: {ex.Message}");
				Environment.Exit(4);
				return;
			}

			if (json)
			{
				Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, JsonOptions));
			}
			else
			{
				PrintHumanReadable(report, floor, minSample);
			}

			Environment.Exit(report.EnforceReady ? 0 : 1);
		}, labelsOption, floorOption, minSampleOption, jsonOption);

		return cmd;
	}

	private static List<LabeledCandidate> LoadLabels(FileInfo file)
	{
		var rows = new List<LabeledCandidate>();
		using var reader = new StreamReader(file.OpenRead(), Encoding.UTF8);

		string? line;
		var lineNo = 0;
		var headerSeen = false;
		while ((line = reader.ReadLine()) != null)
		{
			lineNo++;
			var trimmed = line.Trim();
			if (trimmed.Length == 0 || trimmed.StartsWith('#'))
			{
				continue;
			}

			var parts = trimmed.Split(',', 2);
			if (parts.Length != 2)
			{
				throw new FormatException($"line {lineNo}: expected two CSV fields, got '{trimmed}'");
			}

			if (!headerSeen)
			{
				headerSeen = true;
				if (string.Equals(parts[0].Trim(), "kind", StringComparison.OrdinalIgnoreCase))
				{
					continue; // skip header
				}

				// Else: no header; the first row is data, fall through.
			}

			var kind = parts[0].Trim();
			var rawLabel = parts[1].Trim();
			if (!TryParseBool(rawLabel, out var isTp))
			{
				throw new FormatException($"line {lineNo}: '{rawLabel}' is not a valid boolean (true/false/yes/no/1/0)");
			}

			rows.Add(new LabeledCandidate(kind, isTp));
		}

		return rows;
	}

	private static bool TryParseBool(string raw, out bool value)
	{
		switch (raw.ToLowerInvariant())
		{
			case "true":
			case "yes":
			case "1":
			case "tp":
			case "positive":
				value = true;
				return true;
			case "false":
			case "no":
			case "0":
			case "fp":
			case "negative":
				value = false;
				return true;
			default:
				value = false;
				return false;
		}
	}

	private static void PrintHumanReadable(PrecisionReport report, double floor, int minSample)
	{
		Console.WriteLine();
		Console.WriteLine("Secret-redactor precision sampling");
		Console.WriteLine("==================================");
		Console.WriteLine($"  total labeled : {report.TotalLabeled}");
		Console.WriteLine($"  overall       : {report.OverallPrecision.ToString("F3", CultureInfo.InvariantCulture)}");
		Console.WriteLine($"  floor         : {floor.ToString("F3", CultureInfo.InvariantCulture)}");
		Console.WriteLine($"  min sample    : {minSample}");
		Console.WriteLine();
		Console.WriteLine("Per kind:");
		Console.WriteLine($"  {"kind",-30} {"tp",4} {"fp",4} {"precision",9}");
		foreach (var k in report.PerKind)
		{
			Console.WriteLine(
				$"  {k.Kind,-30} {k.TruePositives,4} {k.FalsePositives,4} {k.Precision.ToString("F3", CultureInfo.InvariantCulture),9}");
		}

		Console.WriteLine();
		Console.WriteLine("Verdict:");
		foreach (var reason in report.Reasons)
		{
			Console.WriteLine($"  - {reason}");
		}

		Console.WriteLine();
		Console.WriteLine(report.EnforceReady
			? "ENFORCE-READY. Operator may flip Mcp:AskGuard:RedactionMode to Enforce."
			: "NOT READY. Keep RedactionMode in Shadow; address the reasons above and re-sample.");
	}
}
