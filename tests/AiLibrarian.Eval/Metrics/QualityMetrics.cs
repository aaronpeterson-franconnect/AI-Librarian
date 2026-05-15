using AiLibrarian.Domain.Citations;

namespace AiLibrarian.Eval.Metrics;

/// <summary>
/// Aggregates <see cref="IClaimGrader"/> verdicts into the synthesis-
/// quality figures the hardening plan's CI gate consumes:
/// <list type="bullet">
///   <item><c>Supported</c> share — share of graded claims the LLM
///         judge accepted as fully supported.</item>
///   <item><c>Partial + NotSupported</c> share — the regression signal
///         the librarian dashboard surfaces.</item>
///   <item><c>Unverifiable</c> share — data-quality signal (golden
///         cases that don't have enough chunk context for a verdict).</item>
/// </list>
/// Designed to be called after every eval run that produced claims,
/// not just on calibration runs — runs without claims get an empty
/// aggregate.
/// </summary>
public static class QualityMetrics
{
	/// <summary>Aggregate the supplied grades into per-verdict counts and shares.</summary>
	public static QualityAggregate Aggregate(IReadOnlyCollection<ClaimGrade> grades)
	{
		ArgumentNullException.ThrowIfNull(grades);

		var supported = 0;
		var notSupported = 0;
		var partial = 0;
		var unverifiable = 0;

		foreach (var g in grades)
		{
			switch (g.Verdict)
			{
				case ClaimVerdict.Supported: supported++; break;
				case ClaimVerdict.NotSupported: notSupported++; break;
				case ClaimVerdict.Partial: partial++; break;
				default: unverifiable++; break;
			}
		}

		var total = grades.Count;
		return new QualityAggregate(
			Total: total,
			Supported: supported,
			NotSupported: notSupported,
			Partial: partial,
			Unverifiable: unverifiable,
			SupportedShare: Share(supported, total),
			NotSupportedShare: Share(notSupported, total),
			PartialShare: Share(partial, total),
			UnverifiableShare: Share(unverifiable, total));
	}

	private static double Share(int value, int total) => total == 0 ? 0.0 : (double)value / total;
}

/// <summary>Aggregate of one batch of <see cref="ClaimGrade"/>s.</summary>
public sealed record QualityAggregate(
	int Total,
	int Supported,
	int NotSupported,
	int Partial,
	int Unverifiable,
	double SupportedShare,
	double NotSupportedShare,
	double PartialShare,
	double UnverifiableShare);
