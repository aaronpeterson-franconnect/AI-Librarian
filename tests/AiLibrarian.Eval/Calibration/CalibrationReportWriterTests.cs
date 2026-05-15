using System.Text.Json;

using AiLibrarian.Domain.Citations;

namespace AiLibrarian.Eval.Calibration;

/// <summary>
/// Pins the calibration JSON report shape. The workflow's gate logic
/// parses these fields by name; a rename without updating the
/// workflow YAML in the same PR would break the gate silently.
/// </summary>
public sealed class CalibrationReportWriterTests
{
	private static readonly DateTimeOffset FixedAt = new(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Empty_report_yields_zero_metrics_and_unknown_band()
	{
		var report = new CalibrationReport(
			Outcomes: Array.Empty<CalibrationOutcome>(),
			CohenKappa: 0.0,
			ObservedAgreement: 0.0);

		var dto = ParseJson(CalibrationReportWriter.ToJson(report, FixedAt));

		dto.GetProperty("totalCases").GetInt32().Should().Be(0);
		dto.GetProperty("agreements").GetInt32().Should().Be(0);
		dto.GetProperty("disagreements").GetInt32().Should().Be(0);
		dto.GetProperty("cohenKappa").GetDouble().Should().Be(0.0);
		dto.GetProperty("band").GetString().Should().Be("Poor",
			"κ = 0.0 lands in the Poor band per the rubric; Unknown is reserved for NaN");
		dto.GetProperty("confusion").GetArrayLength().Should().Be(0);
		dto.GetProperty("cases").GetArrayLength().Should().Be(0);
	}

	[Theory]
	[InlineData(0.95, "NearPerfect")]
	[InlineData(0.80, "NearPerfect")]
	[InlineData(0.75, "Substantial")]
	[InlineData(0.70, "Substantial")]
	[InlineData(0.50, "Moderate")]
	[InlineData(0.40, "Moderate")]
	[InlineData(0.30, "Poor")]
	[InlineData(0.00, "Poor")]
	[InlineData(-0.10, "Poor")]
	public void Band_classification_follows_rubric_thresholds(double kappa, string expectedBand)
	{
		CalibrationReportWriter.ClassifyBand(kappa).Should().Be(expectedBand);
	}

	[Fact]
	public void NaN_kappa_classifies_as_Unknown_and_serialises_as_zero()
	{
		var report = new CalibrationReport(
			Outcomes: new[] { OneOutcome(ClaimVerdict.Supported, ClaimVerdict.Supported) },
			CohenKappa: double.NaN,
			ObservedAgreement: 1.0);

		var json = CalibrationReportWriter.ToJson(report, FixedAt);
		var dto = ParseJson(json);

		dto.GetProperty("band").GetString().Should().Be("Unknown");
		dto.GetProperty("cohenKappa").GetDouble().Should().Be(0.0,
			"the writer normalises NaN to 0.0 so the JSON is downstream-parseable");
	}

	[Fact]
	public void Confusion_matrix_only_emits_nonzero_cells()
	{
		// Two agreements (Supported,Supported) and one disagreement
		// (NotSupported,Partial). Three nonzero cells expected.
		var outcomes = new[]
		{
			OneOutcome(ClaimVerdict.Supported, ClaimVerdict.Supported),
			OneOutcome(ClaimVerdict.Supported, ClaimVerdict.Supported),
			OneOutcome(ClaimVerdict.NotSupported, ClaimVerdict.Partial),
		};
		var report = new CalibrationReport(outcomes, CohenKappa: 0.5, ObservedAgreement: 0.667);

		var dto = ParseJson(CalibrationReportWriter.ToJson(report, FixedAt));
		var confusion = dto.GetProperty("confusion");

		confusion.GetArrayLength().Should().Be(2,
			"Supported,Supported has count=2 and NotSupported,Partial has count=1; both nonzero cells emit one row each");
		var rows = confusion.EnumerateArray()
			.Select(el => new
			{
				Human = el.GetProperty("humanVerdict").GetString(),
				Judge = el.GetProperty("judgeVerdict").GetString(),
				Count = el.GetProperty("count").GetInt32(),
			})
			.ToList();
		rows.Should().ContainEquivalentOf(new { Human = "Supported", Judge = "Supported", Count = 2 });
		rows.Should().ContainEquivalentOf(new { Human = "NotSupported", Judge = "Partial", Count = 1 });
	}

	[Fact]
	public void Per_case_rows_carry_human_judge_confidence_and_agreement_flag()
	{
		var outcomes = new[]
		{
			OneOutcome(ClaimVerdict.Supported, ClaimVerdict.Supported, judgeConfidence: 0.9, rationale: "Cited chunk grounds the claim.", id: "case-A"),
			OneOutcome(ClaimVerdict.Partial, ClaimVerdict.NotSupported, judgeConfidence: 0.6, rationale: "Disagreement.", id: "case-B"),
		};
		var report = new CalibrationReport(outcomes, CohenKappa: 0.4, ObservedAgreement: 0.5);

		var dto = ParseJson(CalibrationReportWriter.ToJson(report, FixedAt));
		var cases = dto.GetProperty("cases").EnumerateArray().ToList();

		cases.Should().HaveCount(2);
		cases[0].GetProperty("id").GetString().Should().Be("case-A");
		cases[0].GetProperty("agreed").GetBoolean().Should().BeTrue();
		cases[0].GetProperty("judgeConfidence").GetDouble().Should().Be(0.9);
		cases[1].GetProperty("agreed").GetBoolean().Should().BeFalse();
		cases[1].GetProperty("humanVerdict").GetString().Should().Be("Partial");
		cases[1].GetProperty("judgeVerdict").GetString().Should().Be("NotSupported");
	}

	[Fact]
	public void Long_rationale_is_truncated_in_the_emitted_row()
	{
		var longRationale = new string('x', 400);
		var outcomes = new[]
		{
			OneOutcome(ClaimVerdict.Supported, ClaimVerdict.Supported, judgeConfidence: 0.8, rationale: longRationale, id: "case-long"),
		};
		var report = new CalibrationReport(outcomes, CohenKappa: 1.0, ObservedAgreement: 1.0);

		var dto = ParseJson(CalibrationReportWriter.ToJson(report, FixedAt));
		var rationale = dto.GetProperty("cases")[0].GetProperty("judgeRationale").GetString();

		rationale.Should().NotBeNull();
		rationale!.Length.Should().BeLessThanOrEqualTo(281, "writer truncates beyond 280 chars and appends an ellipsis");
		rationale.Should().EndWith("…");
	}

	[Fact]
	public async Task WriteAsync_writes_well_formed_json_file()
	{
		var report = new CalibrationReport(
			Outcomes: new[] { OneOutcome(ClaimVerdict.Supported, ClaimVerdict.Supported) },
			CohenKappa: 1.0,
			ObservedAgreement: 1.0);

		var path = Path.Combine(Path.GetTempPath(), $"cal-{Guid.NewGuid():N}.json");
		try
		{
			await CalibrationReportWriter.WriteAsync(report, path, FixedAt);

			File.Exists(path).Should().BeTrue();
			var raw = await File.ReadAllTextAsync(path);
			var parsed = JsonDocument.Parse(raw);
			parsed.RootElement.GetProperty("totalCases").GetInt32().Should().Be(1);
			parsed.RootElement.GetProperty("band").GetString().Should().Be("NearPerfect");
		}
		finally
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
	}

	// --- helpers ---

	private static CalibrationOutcome OneOutcome(
		ClaimVerdict human,
		ClaimVerdict judge,
		double judgeConfidence = 1.0,
		string rationale = "",
		string id = "case")
	{
		var c = new CalibrationCase(
			Id: id,
			ClaimText: "claim",
			CitedChunks: Array.Empty<CalibrationChunk>(),
			HumanVerdict: human,
			HumanConfidence: 1.0,
			HumanRationale: string.Empty,
			Tags: new Dictionary<string, string>(StringComparer.Ordinal));

		var grade = new ClaimGrade(
			ClaimId: Guid.NewGuid(),
			Verdict: judge,
			Confidence: judgeConfidence,
			Rationale: rationale);

		return new CalibrationOutcome(c, grade);
	}

	private static JsonElement ParseJson(string raw)
		=> JsonDocument.Parse(raw).RootElement.Clone();
}
