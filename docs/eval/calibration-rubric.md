# LLM-Judge Calibration Rubric

> Companion to [`docs/eval/README.md`](README.md). Defines the human-grading
> rubric used to produce the **calibration set** — the held-out
> ground truth that the eval harness measures the LLM judge against on
> every run, gating the CI **judge inter-rater agreement** metric.

## Why this rubric exists

The eval harness's `LlmClaimGrader` (`AiLibrarian.Quality.LlmClaimGrader`)
emits one of four verdicts per claim:

| Verdict | Meaning |
|---|---|
| `Supported` | The cited chunks substantively support the claim. |
| `NotSupported` | The cited chunks do not support the claim (contradict, are off-topic, or don't reach the claim). |
| `Partial` | Some of the claim is supported by the cited chunks; some is not. |
| `Unverifiable` | The cited chunks are too vague, too short, or otherwise insufficient to reach a verdict. |

The hardening plan's quality gate warns when judge-vs-human agreement
drops below **Cohen's κ = 0.7**. That gate has zero meaning until at
least one trained human has labelled the calibration set the same way
the judge will. This rubric is what that human is trained against.

**Discipline matters more than perfection.** Inter-rater agreement
between two humans against a shared rubric is the ceiling the LLM
judge can ever reach; if our humans don't agree with each other, the
gate is noise.

## The rules

Apply these rules **in order**. The first rule that fires determines
the verdict.

### Rule 1 — Does the cited chunk **contradict** the claim?

If a cited chunk states the opposite of the claim, OR states a value
that's clearly inconsistent (different numbers, different actors,
different procedures), the verdict is **`NotSupported`**.

Example:
- Claim: "Staging Postgres passwords rotate every 90 days."
- Chunk: "Credential rotation cadence: staging 30 days, production 7 days."
- → **NotSupported** (claim's interval contradicts the chunk).

### Rule 2 — Are the cited chunks **off-topic**?

If the cited chunks don't address what the claim is about — they talk
about a different system, a different concept, a different timeframe
— the verdict is **`NotSupported`**.

Example:
- Claim: "The ingest worker drains the Service Bus queue at 100 msg/s."
- Chunk: "The portal upload endpoint accepts files up to 25 MB."
- → **NotSupported** (chunk doesn't address the ingest worker or its throughput).

### Rule 3 — Are the cited chunks **insufficient** (too vague or too short to support the claim)?

If the chunks gesture in the right direction but don't carry the specific
content the claim asserts — they hint, they reference, they list
without explaining — the verdict is **`Unverifiable`**.

Example:
- Claim: "Source classification is enforced at the database layer via row-level security predicates."
- Chunk: "See the RLS policy file for current rules."
- → **Unverifiable** (chunk is a pointer, not a description).

Note: `Unverifiable` is **not** the polite fallback for "I'm not sure."
A human who hesitates between `Supported` and `NotSupported` should
pick `Partial`, not `Unverifiable`. Reserve `Unverifiable` for cases
where the cited content is genuinely too thin to grade.

### Rule 4 — Is **only part** of the claim supported?

If the claim is compound (states two or three things) and the cited
chunks support some but not all of them, the verdict is **`Partial`**.

Example:
- Claim: "The cascade-regeneration worker runs every hour and tackles up to 50 facets per tick."
- Chunk: "The cascade worker's default `WikiMaintenance:Interval` is `01:00:00`."
- → **Partial** (chunk supports the cadence but not the per-tick cap).

Apply Rule 4 also when the claim contains a quantitative qualifier
("most", "always", "only") that's broader than what the chunk states.

### Rule 5 — Default: **Supported**

If none of rules 1-4 fire, the cited chunks substantively support the
claim. Verdict: **`Supported`**.

A grader landing on `Supported` should be able to point at the exact
sentence(s) in the cited chunk that ground the claim.

## What graders should NOT do

- **Do not use prior knowledge.** Grade against the cited chunks alone.
  If the claim is true in the wider world but the cited chunks don't
  show it, the verdict is still `NotSupported` or `Unverifiable`.
- **Do not penalize style.** A claim phrased awkwardly or with extra
  hedging still gets graded on whether the chunks support it.
- **Do not pre-judge the judge.** Don't try to predict what the LLM
  will say and either match it or rebel against it. Grade the claim,
  not the agreement metric.
- **Do not pick `Unverifiable` to avoid commitment.** That bias inflates
  the chance-agreement floor and ruins κ. Force the decision per the
  rules above.

## Confidence

Each grader-emitted record carries a self-reported confidence in [0, 1].
For the human side, use this scale:

| Confidence | Meaning |
|---|---|
| 1.0 | Verdict is obvious from the chunk; no ambiguity. |
| 0.8 | Confident, but rephrasing the claim slightly could change the verdict. |
| 0.6 | Borderline; verdict could plausibly go the other way. |
| 0.4 | Unsure; default to the more conservative verdict. |

Cohen's κ in the eval harness uses verdicts only — confidence is
recorded for triage when agreement is low (high-confidence
disagreements are the cases worth post-mortem-ing).

## Calibration set authoring guidelines

1. **20 cases minimum.** Below that the κ estimate has too much variance.
2. **Cover all four verdicts.** Target roughly: 8 Supported, 5 NotSupported,
   4 Partial, 3 Unverifiable. Don't go all-`Supported` even if your corpus
   trends that way — the metric needs variance in both raters.
3. **Pull from real claims.** Run the synthesis pipeline, capture
   real LLM-emitted claims with their citations, and grade those.
   Synthetic claims drift toward unrealistic shapes.
4. **One claim per case.** Same as the golden-set rule: compound
   claims confuse the metric.
5. **Two humans, independently.** Don't review each other's labels
   before sealing the calibration set. After both have labelled,
   reconcile disagreements case-by-case with the rules above; the
   reconciled label is the gold.

## When the gate trips

Cohen's κ < 0.7 → warning, not a hard fail. The recovery steps:

1. **Look at the confusion matrix in the eval report.** Which verdicts
   is the judge confusing? `Supported` vs `Partial` is a different
   problem than `NotSupported` vs `Unverifiable`.
2. **Re-grade the disagreements as a pair of humans.** If the humans
   reconvene and decide the gold was wrong on some cases, update them.
   That's normal calibration drift; commit the new gold with a
   reason.
3. **If the judge is systematically wrong on a class** (e.g. it calls
   everything `Supported` regardless of citation strength) the prompt
   needs work — open an issue against
   `src/AiLibrarian.Quality/LlmClaimGrader.cs:SystemPrompt`.
4. **If the calibration set is too small or too easy**, add more cases
   from real PRs and re-run.

## File layout

- Calibration cases: `tests/AiLibrarian.Eval/golden-sets/calibration/*.yaml`
- Loader: `tests/AiLibrarian.Eval/Calibration/CalibrationCaseLoader.cs`
- Runner: `tests/AiLibrarian.Eval/Calibration/CalibrationRunner.cs`
- Metric: `tests/AiLibrarian.Eval/Metrics/CohenKappa.cs`
- This rubric: `docs/eval/calibration-rubric.md`
