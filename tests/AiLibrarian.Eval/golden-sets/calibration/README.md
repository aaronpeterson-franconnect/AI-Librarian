# Calibration set

> The 20 human-graded `(claim, cited-chunks)` cases that the eval
> harness measures the LLM judge against. Drives the
> `judge_inter_rater_agreement` CI gate per the Phase 0 hardening plan.

See [`docs/eval/calibration-rubric.md`](../../../../docs/eval/calibration-rubric.md)
for the grading rules.

## Layout

20 starter YAMLs (`01-…` through `20-…`), distributed across verdicts:

| Verdict | Count | Files |
|---|---|---|
| `Supported` | 8 | 01–08 |
| `NotSupported` | 5 | 09–13 |
| `Partial` | 4 | 14–17 |
| `Unverifiable` | 3 | 18–20 |

The verdict counts are deliberate. The hardening plan calls for at
least 20 cases with all four verdicts represented; if you skew toward
one verdict, Cohen's κ collapses toward its chance-agreement floor
and the gate stops discriminating regression from noise.

## Schema

```yaml
id: <stable-identifier, defaults to filename stem>
claim_text: <the claim being graded — what the LLM judge will see>
cited_chunks:
  - chunk_id: <GUID, stable across runs>
    text: |
      <chunk content the grader sees>
human_verdict: <Supported | NotSupported | Partial | Unverifiable>
human_confidence: <float in [0, 1]; defaults to 1.0>
human_rationale: <one sentence; why this verdict>
tags:
  <key>: <value>
```

Tags are free-form. The starter set uses:
- `verdict_class`: redundant with `human_verdict`, kept for filter convenience.
- `category`: `factual`, `contradiction`, `off_topic`, `compound`, `qualifier`, `pointer`, `vague`, `too_short`.
- `difficulty`: `easy`, `medium`, `hard` — for stratified analysis of where the judge struggles.
- `area`: subject area (`rls`, `audit`, `ingest`, etc.) when relevant.

## Authoring discipline

Two humans, independently. After both have labelled, reconcile any
disagreements per the rubric — the reconciled label is the gold. If
the humans can't agree even after reconciliation, drop the case; an
ambiguous gold poisons κ.

**Do not author cases by reading the judge's verdict first.** The
calibration set's value depends on the humans being independent of
the judge — see the rubric.

## Running the calibration

The harness's `CalibrationRunner` loads every YAML under this directory,
runs the configured `IClaimGrader` over each one, and emits a
`CalibrationReport` with Cohen's κ and a confusion matrix.

CI gate (warn-only per the hardening plan):

- κ ≥ 0.7 — pass
- κ < 0.7 — warn; investigate per the rubric's "When the gate trips" section
- κ < 0.4 — escalate; the calibration set or the judge prompt needs work

## Growing the set

The starter 20 is a floor, not a ceiling. After Phase 1 starts producing
real LLM-emitted claims, mine the eval harness's run logs for cases the
judge graded with low confidence — those are the natural candidates for
expanding the calibration set. Add them with `21-…`, `22-…`, etc.; the
loader picks them up automatically.

When the set passes ~50 cases, consider rebalancing the verdict mix to
match the real-corpus distribution rather than the deliberately-balanced
starter mix.
