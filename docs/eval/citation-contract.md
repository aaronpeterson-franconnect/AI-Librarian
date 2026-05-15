# Citation contract — validator, grader, dangling detector

> Operational notes for `src/AiLibrarian.Quality/`. ADR 0007 names the
> contract; this doc captures how the implementations split, what the
> open items resolved to, and what is deliberately deferred to Phase 2.

## What ships today (Pre-Phase-1)

- **`ICitationValidator` + `CitationValidator`** — the mechanical five-rule
  checker from ADR 0007. Storage-agnostic; depends on `IChunkLookup`.
  Runs on every synthesis call (cheap, no LLM).
- **`IClaimGrader` + `LlmClaimGrader`** — LLM-as-judge spot-check grader.
  Calls `IChatProvider` with a structured-output prompt; parses the
  first JSON object out of the response. Robust to model chatter and
  prompt-injection inside the cited chunk text (system prompt frames
  source excerpts as data, not instructions).
- **`DanglingCitationDetector`** — in-process sweep that resolves each
  citation's chunk and reports the missing/soft-deleted ones. Phase 4's
  Cascade-Regeneration Worker will call this on a schedule.
- **`InMemoryClaimGradeSink`** — pre-Phase-2 holding pen for grader
  verdicts so the eval harness has somewhere to write them.

## Open items — resolved

| Item | Resolution |
|------|------------|
| **Liquibase `wiki_claim_grades` timing** | ✅ Landed as migration `0025-wiki-claim-grades.sql` (Phase 2 schema). `PostgresClaimGradeSink` in `AiLibrarian.Infrastructure` persists into it via the `IClaimGradeSink` interface; the in-memory sink remains for the eval harness's dev-without-Postgres path. |
| **Dangling-citation SQL function** | ✅ Landed as `audit_dangling_citations(department_id, since)` in migration `0026-wiki-dangling-citations-fn.sql`. Queryable from the Cascade-Regeneration Worker (Phase 4) and the librarian dashboard. Returns `(claim_id, citation_id, chunk_id, reason)` rows with `reason in ('chunk_missing', 'source_soft_deleted')`. SECURITY INVOKER — the caller's RLS context bounds the result. |
| **`IAuditQueryService` for Phase 2 dashboard** | Interface defined in `AiLibrarian.Auditing` (already shipped during the PostgresAuditWriter work, item #3); concrete read implementation is Phase 2. |

## Acceptance the validator must hit

From the pre-Phase-1 hardening plan:

1. **Synthetic claim corpus** — 50 valid + 50 each-rule-violating claims,
   all classified correctly by `CitationValidator`. Lives in
   `tests/AiLibrarian.Quality.Tests/Corpus/`.
2. **Grader calibration** — agreement with calibration humans ≥ 0.7
   inter-rater reliability on the eval harness's 20-case calibration
   set. (Calibration sign-off is Open Item #3 in the plan; the eval
   harness reports the figure on every nightly run.)
3. **Dangling-citation detector** — returns correct rows when source
   chunks are soft-deleted in test fixtures. Exercised by
   `DanglingCitationDetectorTests`.

## Wiring it together (eval harness)

`AiLibrarian.Eval`'s synthesis-metrics path imports `IClaimGrader` and
records every grader call into `InMemoryClaimGradeSink`. The harness's
report aggregates:

- **Citation coverage** (% claims with citations) — mechanical, from `CitationValidator`.
- **Citation precision** (% Supported / total graded) — from `LlmClaimGrader` + sink.
- **Refusal-when-no-source rate** (% claims with no citation marked refused) — derived.

These are the three synthesis metrics from item #1 of the hardening plan.
