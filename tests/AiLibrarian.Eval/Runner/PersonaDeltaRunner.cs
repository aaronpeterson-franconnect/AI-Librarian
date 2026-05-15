using AiLibrarian.Eval.Metrics;

namespace AiLibrarian.Eval.Runner;

/// <summary>
/// Runs every golden case through two backends -- one "neutral" (no
/// persona) and one persona-aware -- and reports the delta. Answers
/// the question the hardening plan flags as the persona-awareness
/// success criterion:
///
/// <para><em>"Same set runs neutral and Engineering-persona; deltas
/// tracked."</em></para>
///
/// <para><b>Polarity convention.</b> All delta metrics are computed as
/// <c>persona - neutral</c>. Positive = persona is better; negative =
/// persona is worse. A persona profile that systematically hurts
/// recall is a bad profile; this runner is how that shows up.</para>
///
/// <para>Retrieval-only by design. The runner computes recall@k,
/// MRR, and nDCG@k under each backend; it does NOT consult synthesis
/// metrics (citation coverage / refusal rate) because those don't
/// vary by persona retrieval profile alone — they're sensitive to
/// the LLM, the prompt, and many other things. Persona-aware
/// synthesis quality is the calibration set's job (κ vs. human).</para>
/// </summary>
public sealed class PersonaDeltaRunner
{
	private readonly EvalRunnerOptions _options;

	/// <summary>Creates the runner with default thresholds.</summary>
	public PersonaDeltaRunner()
		: this(new EvalRunnerOptions())
	{
	}

	/// <summary>Creates the runner with custom thresholds (CI gate parameters).</summary>
	public PersonaDeltaRunner(EvalRunnerOptions options)
	{
		_options = options;
	}

	/// <summary>
	/// Run <paramref name="cases"/> through <paramref name="neutralBackend"/>
	/// and <paramref name="personaBackend"/> and aggregate the per-case
	/// retrieval deltas.
	/// </summary>
	/// <param name="cases">The golden cases to evaluate.</param>
	/// <param name="neutralBackend">Backend exercising the "no persona" retrieval path.</param>
	/// <param name="personaBackend">Backend exercising the persona-aware retrieval path.</param>
	/// <param name="persona">Persona name being measured (stamped on the report; usage by callers + JSON consumers).</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	public async Task<PersonaDeltaReport> RunAsync(
		IReadOnlyList<GoldenCase> cases,
		RetrievalBackend neutralBackend,
		RetrievalBackend personaBackend,
		string persona,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(cases);
		ArgumentNullException.ThrowIfNull(neutralBackend);
		ArgumentNullException.ThrowIfNull(personaBackend);
		ArgumentException.ThrowIfNullOrWhiteSpace(persona);

		if (cases.Count == 0)
		{
			return PersonaDeltaReport.Empty(persona, _options.RecallK);
		}

		var rows = new List<PersonaDeltaCaseRow>(cases.Count);
		var neutralRecallSum = 0.0;
		var personaRecallSum = 0.0;
		var neutralMrrSum = 0.0;
		var personaMrrSum = 0.0;
		var neutralNdcgSum = 0.0;
		var personaNdcgSum = 0.0;
		var improved = 0;
		var degraded = 0;
		var unchanged = 0;
		var topOneChanged = 0;

		foreach (var c in cases)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var neutralOutcome = await neutralBackend(c, cancellationToken).ConfigureAwait(false);
			var personaOutcome = await personaBackend(c, cancellationToken).ConfigureAwait(false);

			var expected = c.ExpectedChunkIds.ToHashSet();
			var nRecall = RetrievalMetrics.RecallAtK(neutralOutcome.RetrievedChunkIds, expected, _options.RecallK);
			var pRecall = RetrievalMetrics.RecallAtK(personaOutcome.RetrievedChunkIds, expected, _options.RecallK);
			var nMrr = RetrievalMetrics.ReciprocalRank(neutralOutcome.RetrievedChunkIds, expected);
			var pMrr = RetrievalMetrics.ReciprocalRank(personaOutcome.RetrievedChunkIds, expected);
			var grades = c.RelevanceGrades();
			var nNdcg = RetrievalMetrics.NDcgAtK(neutralOutcome.RetrievedChunkIds, grades, _options.RecallK);
			var pNdcg = RetrievalMetrics.NDcgAtK(personaOutcome.RetrievedChunkIds, grades, _options.RecallK);

			neutralRecallSum += nRecall;
			personaRecallSum += pRecall;
			neutralMrrSum += nMrr;
			personaMrrSum += pMrr;
			neutralNdcgSum += nNdcg;
			personaNdcgSum += pNdcg;

			var recallDelta = pRecall - nRecall;
			if (recallDelta > 0)
			{
				improved++;
			}
			else if (recallDelta < 0)
			{
				degraded++;
			}
			else
			{
				unchanged++;
			}

			var neutralTop = neutralOutcome.RetrievedChunkIds.Count > 0 ? (Guid?)neutralOutcome.RetrievedChunkIds[0] : null;
			var personaTop = personaOutcome.RetrievedChunkIds.Count > 0 ? (Guid?)personaOutcome.RetrievedChunkIds[0] : null;
			var topChanged = neutralTop != personaTop;
			if (topChanged)
			{
				topOneChanged++;
			}

			var positionImprovement = PersonaDeltaMetrics.PositionImprovement(
				neutralOutcome.RetrievedChunkIds,
				personaOutcome.RetrievedChunkIds,
				expected);

			rows.Add(new PersonaDeltaCaseRow(
				CaseId: c.Id,
				NeutralRecallAtK: nRecall,
				PersonaRecallAtK: pRecall,
				RecallDelta: recallDelta,
				NeutralReciprocalRank: nMrr,
				PersonaReciprocalRank: pMrr,
				ReciprocalRankDelta: pMrr - nMrr,
				NeutralNDcgAtK: nNdcg,
				PersonaNDcgAtK: pNdcg,
				NDcgDelta: pNdcg - nNdcg,
				PositionImprovement: positionImprovement,
				NeutralTopChunkId: neutralTop,
				PersonaTopChunkId: personaTop,
				TopOneChanged: topChanged));
		}

		var count = cases.Count;
		return new PersonaDeltaReport(
			Persona: persona,
			CaseCount: count,
			RecallK: _options.RecallK,
			NeutralRecallAtKAverage: neutralRecallSum / count,
			PersonaRecallAtKAverage: personaRecallSum / count,
			RecallDeltaAverage: (personaRecallSum - neutralRecallSum) / count,
			NeutralMeanReciprocalRank: neutralMrrSum / count,
			PersonaMeanReciprocalRank: personaMrrSum / count,
			MeanReciprocalRankDelta: (personaMrrSum - neutralMrrSum) / count,
			NeutralNDcgAtKAverage: neutralNdcgSum / count,
			PersonaNDcgAtKAverage: personaNdcgSum / count,
			NDcgDeltaAverage: (personaNdcgSum - neutralNdcgSum) / count,
			CasesImproved: improved,
			CasesDegraded: degraded,
			CasesUnchanged: unchanged,
			TopOneChangedCount: topOneChanged,
			Cases: rows);
	}
}

/// <summary>Aggregate report from a <see cref="PersonaDeltaRunner"/> run.</summary>
public sealed record PersonaDeltaReport(
	string Persona,
	int CaseCount,
	int RecallK,
	double NeutralRecallAtKAverage,
	double PersonaRecallAtKAverage,
	double RecallDeltaAverage,
	double NeutralMeanReciprocalRank,
	double PersonaMeanReciprocalRank,
	double MeanReciprocalRankDelta,
	double NeutralNDcgAtKAverage,
	double PersonaNDcgAtKAverage,
	double NDcgDeltaAverage,
	int CasesImproved,
	int CasesDegraded,
	int CasesUnchanged,
	int TopOneChangedCount,
	IReadOnlyList<PersonaDeltaCaseRow> Cases)
{
	/// <summary>An empty report for the no-cases edge case.</summary>
	public static PersonaDeltaReport Empty(string persona, int recallK) => new(
		Persona: persona,
		CaseCount: 0,
		RecallK: recallK,
		NeutralRecallAtKAverage: 0,
		PersonaRecallAtKAverage: 0,
		RecallDeltaAverage: 0,
		NeutralMeanReciprocalRank: 0,
		PersonaMeanReciprocalRank: 0,
		MeanReciprocalRankDelta: 0,
		NeutralNDcgAtKAverage: 0,
		PersonaNDcgAtKAverage: 0,
		NDcgDeltaAverage: 0,
		CasesImproved: 0,
		CasesDegraded: 0,
		CasesUnchanged: 0,
		TopOneChangedCount: 0,
		Cases: Array.Empty<PersonaDeltaCaseRow>());
}

/// <summary>Per-case delta row in <see cref="PersonaDeltaReport"/>.</summary>
public sealed record PersonaDeltaCaseRow(
	string CaseId,
	double NeutralRecallAtK,
	double PersonaRecallAtK,
	double RecallDelta,
	double NeutralReciprocalRank,
	double PersonaReciprocalRank,
	double ReciprocalRankDelta,
	double NeutralNDcgAtK,
	double PersonaNDcgAtK,
	double NDcgDelta,
	int PositionImprovement,
	Guid? NeutralTopChunkId,
	Guid? PersonaTopChunkId,
	bool TopOneChanged);
