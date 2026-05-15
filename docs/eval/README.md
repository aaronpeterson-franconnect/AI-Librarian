# AI Librarian — Evaluation Harness

> Status: **Phase 0 hardening — scaffold landed 2026-05-05** ·
> Companion to [`architecture.md`](../architecture.md) and the
> Phase 1 hardening plan.

The harness lives at [`tests/AiLibrarian.Eval/`](../../tests/AiLibrarian.Eval/).
Its job is to detect regressions in retrieval and synthesis quality
**before** they reach Phase 1 traffic. Without it every change to the
hybrid search ranker, the persona retrieval profile, the LLM provider,
or the prompt template lands blind — and "verifiable answers" stops
being a claim we can defend.

## What it measures

| Layer | Metric | CI gate |
|---|---|---|
| Retrieval | `recall@10`, `recall@20`, `MRR`, `nDCG@10` | recall@10 regression > 5% absolute → fail |
| Retrieval | per-classification recall (proves RLS-respecting retrieval) | nightly only; alerts on regression |
| Synthesis | citation coverage (% claims with ≥1 citation) | < 95% → fail |
| Synthesis | refusal rate on must-refuse cases | < 90% → fail |
| Synthesis | LLM-as-judge citation precision (week 3) | < 0.85 → warn; landing with citation validator |
| Synthesis | tokens per case | trend signal; not a hard fail |
| Calibration | judge inter-rater agreement vs humans | < 0.7 → warn (data quality, not regression) |

## Golden-set authoring

A golden case is a small declarative file describing one expected
retrieval + synthesis outcome. Authoring guidelines:

1. **Pull from real work.** Each case starts as an answered Engineering
   ticket, runbook step, or post-incident review. Don't invent
   synthetic queries.
2. **One question per case.** Compound queries ("how do we rotate
   secrets and what's the on-call rotation?") confuse the metrics —
   split them.
3. **Three-to-five expected chunks.** More than that and recall@10
   stops discriminating; fewer and one off-target hit dominates the
   score.
4. **Mark must-refuse cases explicitly.** A query like "what's the
   API key for the staging Postgres?" with `must_refuse: true` proves
   the system declines rather than fabricates.
5. **Declare the persona.** Engineering pilot starts with persona =
   `engineering`. Don't mix personas in one file.
6. **Declare classification scope.** Engineering pilot is mostly
   `Internal`; cases that target `Confidential` runbooks must have
   `classification_scope: Confidential` so the harness validates RLS
   filtering.

## Calibration set

The LLM-as-judge grader's scores are noise without an inter-rater
calibration set. We hold out 20 cases (out of the ≥50-case Engineering
seed) and have a senior engineer hand-grade the citation precision on
each. Every eval run reports judge-vs-human agreement; a drop below 0.7
flags the calibration data as stale (typically: model swap, prompt
change, or new content domain).

Calibration cases live at `golden-sets/calibration/*.yaml` with a
schema distinct from the golden cases (claim text + cited chunk
texts + human verdict — no retrieval expectation). See
[`calibration-rubric.md`](calibration-rubric.md) for the
human-grader rubric.

### Running the calibration

The calibration is a separate test from the rest of the eval
harness — it requires real LLM calls and is skipped by default to
keep PR runs token-free. To opt in locally:

```powershell
$env:AILIB_LIVE_CALIBRATION = "1"
$env:AZURE_OPENAI_ENDPOINT  = "https://<your-azure-openai>.openai.azure.com"
$env:AZURE_OPENAI_API_KEY   = "<key>"
$env:AZURE_OPENAI_CHAT_DEPLOYMENT = "<deployment-name>"
# Optional: $env:AZURE_OPENAI_API_VERSION = "2024-08-01-preview"
# Optional: $env:AILIB_CALIBRATION_REPORT_DIR = "C:\some\path"

dotnet test tests/AiLibrarian.Eval/AiLibrarian.Eval.csproj `
    --filter "FullyQualifiedName~LiveCalibration"
```

Without the env vars the test skips cleanly. With them set, the test:
1. Loads the 20 calibration YAMLs
2. Runs each through `LlmClaimGrader` against the configured Azure
   OpenAI deployment
3. Computes Cohen's κ vs. the human gold labels
4. Writes `calibration-report.json` to
   `AILIB_CALIBRATION_REPORT_DIR` (default: the test base directory)

### CI: the `live-calibration.yml` workflow

`.github/workflows/live-calibration.yml` runs the live calibration on
a daily schedule + on `workflow_dispatch`. The same κ→band rubric
gates the workflow:

| Band | κ | Workflow outcome |
|---|---|---|
| `NearPerfect` | ≥ 0.8 | Pass (notice annotation) |
| `Substantial` | ≥ 0.7 | Pass (notice annotation) |
| `Moderate` | 0.4 ≤ κ < 0.7 | **Warn** (warning annotation; doesn't fail the job) |
| `Poor` | < 0.4 | **Fail** (error annotation; fails the job) |
| `Unknown` | NaN | **Fail** (broken report) |

Required secrets on the workflow:
`AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_API_KEY`,
`AZURE_OPENAI_CHAT_DEPLOYMENT`.
Optional: `AZURE_OPENAI_API_VERSION`.

The JSON report is attached as a workflow artifact; operators can
inspect the confusion matrix to see which verdicts the judge is
confusing (the `docs/eval/calibration-rubric.md` "When the gate trips"
section is the recovery playbook).

## CI gates

Two workflows consume the harness output:

- **`eval.yml`** (nightly + on-PR for changed retrieval/synthesis
  paths): runs the full set, posts a metric-delta comment on the PR,
  blocks merge on hard-fail thresholds.
- **`quality-gate`** (consolidated PR check): aggregates eval, RLS
  battery, audit-writer health probe, and `AskGuard` adversarial
  results into one required check.

A deliberate retrieval-quality regression PR (e.g., flipping a
hybrid-vector weight) **must** fail the gate. We test the gate the same
way we test the system — with bad PRs.

## Persona-aware retrieval — delta evaluation

ADR 0015 wires retrieval reranking + synthesis style on a persona
profile. The eval harness asks a different question of that profile:
*"For cases authored against this persona's domain, does the persona
make retrieval better, worse, or the same?"*

`PersonaDeltaRunner` answers it. The runner takes one set of golden
cases and TWO retrieval backends — neutral (no persona) and persona-
aware — runs each case through both, and emits a
`PersonaDeltaReport`:

| Field | Meaning |
|---|---|
| `NeutralRecallAtKAverage`, `PersonaRecallAtKAverage` | Recall@k under each backend (the baseline) |
| `RecallDeltaAverage` | `persona - neutral`; positive = persona is better |
| `MeanReciprocalRankDelta`, `NDcgDeltaAverage` | Same polarity convention |
| `CasesImproved` / `CasesDegraded` / `CasesUnchanged` | Per-case bucket counts based on the recall delta sign |
| `TopOneChangedCount` | How many cases had a different #1 chunk under persona |
| `Cases[]` | Per-case rows including `PositionImprovement` — the sum, across the case's expected chunks, of how many slots they moved up under persona (negative = they moved down) |

Three engineering golden cases (`03-` / `04-` / `05-`) target the
specific reranker dimensions documented in ADR 0015:

| File | Dimension | What it proves |
|---|---|---|
| `03-persona-prefers-approved-runbook` | `authorityBias` | Approved sources (sources.approved_at non-null) outrank drafts |
| `04-persona-prefers-recent-postmortem` | `recencyHalfLifeDays` | Fresher content outranks older content for the same topic |
| `05-persona-prefers-engineering-dept` | `crossDepartmentBoost` | Same-department content outranks cross-department content with similar vocabulary |

The cases are pure ranking tests — they only assert which chunks
*should* appear in the top-k and at what rank, not what the synthesis
should say. The delta runner is therefore a retrieval-quality test;
synthesis quality stays measured by the calibration κ.

### Sample drive

```csharp
var runner = new PersonaDeltaRunner(new EvalRunnerOptions { RecallK = 10 });
var cases = GoldenCaseLoader.LoadAll("golden-sets/engineering/");

// neutralBackend: HttpEvalBackend with no persona; personaBackend:
// HttpEvalBackend that sets PersonaId on its retrieval calls.
var report = await runner.RunAsync(cases, neutralBackend, personaBackend, "engineering");

Console.WriteLine($"Cases improved/degraded/unchanged: {report.CasesImproved}/{report.CasesDegraded}/{report.CasesUnchanged}");
Console.WriteLine($"Recall delta: {report.RecallDeltaAverage:+0.000;-0.000;0.000}");
```

A persona profile that systematically increases `CasesDegraded` is a
bad profile; that's the regression signal the gate would track once
the harness wires this report into the nightly workflow.

## Running against a live API

The `EvalRunner` is backend-agnostic — it takes a
`RetrievalBackend` delegate. Two flavors:

| Flavor | Use when |
|--------|---------|
| **Stub backend** (in tests) | Unit tests that need deterministic retrieval rankings. See `EvalRunnerTests.cs` for the pattern. |
| **`HttpEvalBackend`** | Real integration runs against a deployed API (`/api/search/hybrid` + `/api/ask`). |

Example — point the harness at a local API:

```csharp
using var http = new HttpClient { BaseAddress = new Uri("http://localhost:5071/") };
var backend = new HttpEvalBackend(http, new HttpEvalBackendOptions
{
    BearerToken = Environment.GetEnvironmentVariable("AILIB_ACCESS_TOKEN") ?? string.Empty,
    UseAsk = true,
    RetrievalLimit = 10,
});

var cases = await GoldenCaseLoader.LoadAllAsync("golden-sets/engineering/");
var report = await new EvalRunner().RunAsync(cases, backend.AsDelegate);

Console.WriteLine(HttpEvalBackend.FormatReportSummary(report));
```

Two operational modes:

- **Retrieval-only** (`UseAsk = false`) — no LLM tokens spent; useful
  for the PR-time fast loop that regresses recall@k / MRR / nDCG.
- **Full** (`UseAsk = true`) — fires `/api/ask` per case; produces
  citation-coverage + refusal-rate metrics. Token spend is bounded
  by `MaxChunks * (50 cases) * ~one synthesis each` — budget
  accordingly.

**Pre-flight checks the harness does NOT do:**

- It doesn't validate the API is reachable; the first request's
  failure surfaces as an `InvalidOperationException` with the case
  id in the message.
- It doesn't acquire bearer tokens — the caller supplies one (MSAL
  is the API client's concern; the eval harness stays auth-agnostic
  so the same code path runs in pilot-without-Entra deployments).
- It doesn't seed golden chunks into Postgres. The eval expectation
  is that the corpus is already populated; the harness reads-only.

## What's NOT in scope here

- No golden cases for autonomous-action recommendations — those land
  in Phase 4 with the persona action records.
- No cross-persona synthesis — deferred to v4 per
  [`architecture.md`](../architecture.md).
- No auto-tuning of persona retrieval profiles — bit-rot prevention
  is on the v2+ roadmap; today the eval surfaces drift, humans tune.

## Open items (carried from the hardening plan)

- Engineering persona owner sign-off on the 50-case seed.
- 1-2 hours of senior-engineer time for calibration grading.
- Decision on whether judge-vs-human disagreement triggers a hard
  fail or a warn (today: warn — flip to fail once we trust the
  calibration set).
