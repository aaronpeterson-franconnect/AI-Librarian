# AiLibrarian.Security — AskGuard

Synthesis-time defense layer per [ADR 0017](../../docs/adr/0017-mcp-ask-threat-model.md).

## What lives here

| Type | Purpose |
|------|---------|
| `AskGuard` | Orchestrator. Applies six controls (C1–C6) from ADR 0017 and emits a `AskGuardResult` containing answer, refusal reason, audit fields, redaction candidates. |
| `AskGuardOptions` | Per-tenant knobs: query byte cap, rate limit, redaction mode. Bind from `Mcp:AskGuard` or `Api:AskGuard` config section. |
| `ChunkEnvelope` | Wraps retrieved chunks in `<source>…</source>` markers; neutralizes forged envelope tags inside chunk text. |
| `SecretRedactor` | Pattern-based credential detector. 7 pattern kinds (AWS, GitHub, Slack, Stripe, JWT, PEM/PGP, generic api-key/password assignments). Shadow / Enforce / Off modes. |
| `RateLimiter` | Token-bucket rate limit keyed by Entra OID. |

## Dependency directions

```
        ┌──────────────────────────────┐
        │  AiLibrarian.Security        │  (this project — no infra deps)
        │   • AskGuard                 │
        │   • IAskRetrieval (iface)    │
        │   • IAskSynthesizer (iface)  │
        └──────────┬───────────────────┘
                   │
       ┌───────────┴──────────────────┐
       │                              │
  ┌────▼────────────┐         ┌───────▼─────────────┐
  │ AiLibrarian.Api │         │ AiLibrarian.Mcp.…   │
  │   /api/ask      │         │   tests + corpus    │
  │ + impls of      │         └─────────────────────┘
  │   IAskRetrieval │
  │   IAskSynthesi… │
  └─────────────────┘
```

Security has zero infra dependencies — `IAskRetrieval` and `IAskSynthesizer`
are interfaces. The API supplies concrete implementations that call
`IHybridChunkSearch` + `IChatProvider`; tests supply stubs.

## Wiring in the API

Currently wired into `/api/ask` (see `AiLibrarian.Api/Synthesis/`).
Adds the guard between the retrieval call and the synthesizer call:

1. API embeds the query → vector.
2. `IHybridChunkSearch.SearchAsync` returns RLS-filtered chunks.
3. `ApiAskRetrieval` (the API's `IAskRetrieval`) hands them to AskGuard.
4. AskGuard applies C1 (cap) → C6 (rate) → C2/C3 (envelope + system prompt) → C4 (redact) controls.
5. `ApiAskSynthesizer` (the API's `IAskSynthesizer`) wraps `IChatProvider` for the LLM call.
6. Audit row emitted with `AuditCriticality.Critical` (synthesis is auditable per ADR 0010).

## When AskGuard is not enough

It enforces mechanical contracts. Two things it cannot do, by design:

- **Decide whether an LLM answer is correct.** That's the
  `LlmClaimGrader` in `AiLibrarian.Quality` — spot-check grading runs
  on a calibration sample, not every call.
- **Block jailbreak prompts before the LLM sees them.** The system
  prompt + envelope make refusal *possible*; the LLM itself does the
  refusing. The eval harness's adversarial corpus measures refusal
  rate against a live model.

## Enforce-mode flip workflow

ADR 0017 ships the secret redactor in `Shadow` mode and gates the
flip to `Enforce` on per-tenant precision sampling reaching ≥0.9.
The supporting tooling lives at:

| Piece | Where |
|-------|-------|
| Audit-row export | `deploy/scripts/Export-RedactionCandidates.ps1` |
| Per-kind precision aggregator | `PrecisionSampling.Compute` (this project) |
| CLI driver + verdict | `ailib precision-sampling --labels labeled.csv` |
| Tests pinning the math | `AiLibrarian.Mcp.Tests/Security/PrecisionSamplingTests.cs` |

Workflow:

1. **Wait for the audit ledger to accumulate `ask.answered` /
   `ask.refused` rows with `redaction_candidates > 0`.** A few hundred
   in the relevant tenant is the practical bar; the default
   `minSampleSize=100` enforces this.
2. **Export.** `Export-RedactionCandidates.ps1` writes a CSV of one row
   per ask invocation with non-zero candidates.
3. **Label.** The operator inspects each call (via correlation id /
   application logs) and tags whether each match was a real credential
   (true positive) or a false positive (e.g. a UUID matching the
   credit-card regex). Append `kind,is_true_positive` columns.
4. **Compute.** `ailib precision-sampling --labels labeled.csv` prints
   per-kind precision + an overall verdict. Exits 0 when ENFORCE-READY;
   exits 1 otherwise so a CI gate can wrap it.
5. **Flip.** If ready, change `Mcp:AskGuard:RedactionMode` to
   `Enforce` and restart the API. The transition itself is audited
   (route-level audit fires the next time `ask` runs and the redactor
   mode shows up in the audit details).

The verdict requires:

- **Overall precision ≥ floor** (default 0.9 per ADR 0017).
- **Every kind that fired in the sample ≥ floor** — keeps a single
  noisy kind (e.g. credit-card via UUID false positives) from being
  hidden by a high overall average.
- **Sample size ≥ minimum** (default 100) — small samples are noisy.

Kinds with no samples are noted but don't block — they simply have
no signal yet.

## Status

| Hardening plan item | Status |
|---|---|
| #5 — ADR 0017 + AskGuard + adversarial corpus | ✅ Shipped |
| MCP `ask` tool calling `/api/ask` | ✅ Shipped |
| Output enforce-mode flip per tenant | ✅ Tooling shipped; operator runs the workflow above |
