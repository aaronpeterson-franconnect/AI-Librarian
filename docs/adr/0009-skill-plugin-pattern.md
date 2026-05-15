# ADR 0009 — File-format support is delivered as self-contained Skill plugins

> Status: **Accepted** · Date: 2026-04-29 · Deciders: Architect (initial proposal — to be ratified)

## Context

The corpus must support a broad set of file formats from day 1 (text,
PDF, Office docs, code, SQL, images, audio, video) and we expect new
formats to be added over time (Confluence exports, custom XML
formats, CAD files, …). We need a pattern that:

1. Keeps each format's logic isolated
2. Provides a uniform contract upstream of the format handler
3. Makes adding a new format a localized change
4. Stays within the pure-.NET stack (ADR 0002) — every Skill is a
   .NET assembly; we use Azure-native services for capabilities
   that traditionally lived in Python (transcription, vision)
5. Produces canonical outputs — markdown content + span-aware metadata
   — that the rest of the system treats identically regardless of
   format

The Synthadoc and Beever Atlas projects pioneered the pattern of
self-contained Skill folders with `SKILL.md` manifests. We adopt and
adapt it.

## Decision

Each file format is implemented as a **Skill plugin** with this
structure:

```
src/AiLibrarian.Skills.{Format}/
	SKILL.md                  # human + machine-readable manifest
	AiLibrarian.Skills.{Format}.csproj
	{Format}Skill.cs          # implements ISkill
	Span/                     # format-specific span anchor logic
	Tests/                    # plugin-local unit tests
```

### `SKILL.md` manifest

The manifest is the contract. Example for `Skills.Pdf`:

```markdown
# Skill: PDF

mime_types:
	- application/pdf
extensions:
	- .pdf
capability_tier: full
implementation: dotnet
external_services:
	- AzureAIDocumentIntelligence
default_chunk_strategy: page
default_max_chunk_tokens: 1200
span_format:
	type: object
	properties:
		page: { type: integer, minimum: 1 }
		para: { type: integer, minimum: 0 }
required_role: Contributor
```

Manifests are read at startup by the `SkillRegistry`. `SkillRegistry`
is the only place ingestion services dispatch from — they hand over a
source and get back a `SkillResult`.

### `ISkill` interface

```csharp
public interface ISkill
{
	string Name { get; }
	IReadOnlySet<string> SupportedMimeTypes { get; }
	IReadOnlySet<string> SupportedExtensions { get; }

	Task<SkillResult> CanonicalizeAsync(
		Stream raw,
		SourceMetadata metadata,
		CancellationToken ct);
}

public record SkillResult(
	string CanonicalMarkdown,
	IReadOnlyList<Chunk> Chunks,
	IReadOnlyDictionary<string, JsonNode> ExtractedMetadata,
	IReadOnlyList<SkillIssue> Issues);

public record Chunk(
	string ContentMarkdown,
	JsonNode SpanAnchor,
	int OrderIndex);
```

A `Chunk` is what gets persisted to the `chunks` table; its
`SpanAnchor` is the format-specific JSON that `wiki_claim_citations`
references later.

### External-service dispatch

When a Skill needs an Azure AI service (Speech, Vision, Document
Intelligence) it calls the service directly through its .NET SDK
within the Skill assembly. There is **no Python sidecar pattern**
(see ADR 0002). For long-running calls (e.g., Speech batch
transcription on a multi-hour video), the Skill enqueues an Azure
service job, persists the job ID, and resumes processing when the
job completes — entirely within the .NET host process.

### Adding a new Skill

1. Generate `src/AiLibrarian.Skills.{Format}` from a template
2. Implement `ISkill`
3. Author `SKILL.md`
4. Add the project to the solution and reference it from
   `AiLibrarian.Ingestion.Worker`
5. Drop integration tests with example inputs

No core code changes. The `SkillRegistry` discovers the new plugin via
the manifest at startup.

### Chunk strategy — system default only

Each Skill declares a default chunk strategy and chunk size in its
`SKILL.md` manifest. **There is no per-department override in v1.**
Departments do not configure chunking. This keeps the system simple,
predictable, and the chunk store homogeneous for retrieval tuning.

If a department's use case genuinely requires a different chunk
strategy for the same format (e.g., one team wants per-function code
chunks, another wants per-file), the path forward is to define a new
Skill with the alternate strategy rather than overriding configuration.

#### Day-1 chunk strategy defaults (2026-04-29)

| Skill | Strategy | Default size |
|---|---|---|
| `Skills.Markdown` | Paragraph-grouped | ~500 tokens, 50-token overlap |
| `Skills.Pdf` | Per-page with paragraph boundaries | ~800-1200 tokens, no overlap |
| `Skills.Office` (DOCX) | Per-section if available, else paragraph-grouped | ~500-800 tokens |
| `Skills.Code` | Semantic (per function/class) via Roslyn / TreeSitterSharp | Function-bounded, no token cap |
| `Skills.Sql` | Per Liquibase changeSet; else per statement group | changeSet-bounded |
| `Skills.Media` | Per speaker turn with diarization | ~30-60 s, turn-bounded |
| `Skills.Image` | One chunk per detected region (diagrams) or per-image | Region-bounded |

These defaults are tuning targets, not commitments. Phase 3 includes
empirical evaluation against retrieval-quality metrics (recall@k,
MRR) and will adjust them based on production data. See open
question Q9.

### Source size limits

Each Skill declares a default soft-warn and hard-cap on source size.
Hitting soft-warn flags the source for librarian review; exceeding
the hard cap rejects ingest with a clear error.

Caps are configurable per-department in `policy.yaml`. System-wide
defaults are documented in [`docs/open-questions.md`](../open-questions.md)
under Q10.

### Skill versioning — none in v1

Skills are not versioned. The latest deployed Skill always wins.
Chunks do not record the Skill version that produced them. This is a
deliberate simplicity choice with two acknowledged consequences:

1. After a Skill upgrade, older chunks remain in their original form
   while new ingests use the upgraded logic. The chunk store can
   become temporarily inconsistent until affected sources are
   re-ingested.
2. Quality investigations across time are harder — we cannot ask
   "which Skill version produced this chunk."

If either consequence becomes a real problem, the **upgrade path**
is to add a `produced_by_skill_version` text column to `chunks` and
populate it on new chunks (auditability) without auto-re-chunking
on upgrade (no operational disruption). This is intentionally
deferred.

### Day-1 Skills (Engineering pilot)

| Skill | Implementation | Notes |
|---|---|---|
| `Skills.Markdown` | .NET | Pass-through with frontmatter parsing |
| `Skills.Pdf` | .NET (Azure AI Document Intelligence) | Per-page span anchors |
| `Skills.Office` | .NET (OpenXML SDK) | DOCX, XLSX, PPTX |
| `Skills.Code` | .NET (Roslyn for C#; `TreeSitterSharp` for TS / JS / Python / Go / Rust) | Semantic chunks per function/class |
| `Skills.Sql` | .NET (ANTLR-based) | Liquibase changelog-aware per project conventions |
| `Skills.Image` | .NET (Azure AI Vision) | OCR + structured description |
| `Skills.Media` | .NET (Azure AI Speech for direct uploads; Microsoft Graph for Teams transcripts; VTT parser for both) | Audio + video; timestamp anchors |

## Consequences

### Easier

- New formats land as additive changes; the core never moves.
- Each Skill has its own tests, its own dependencies, its own scaling.
- Each Skill assembly is independently versioned and deployed; an
  Azure AI Speech outage affects `Skills.Media` but doesn't break
  PDF ingest.
- The pattern matches the precedent set by Synthadoc and the Open
  Brain recipes; future contributors recognize it.

### Harder

- We commit to a manifest format and an `ISkill` contract early; both
  will need to evolve carefully (versioned manifest, additive
  interface changes).
- Sidecar handoffs add latency to multimodal ingest paths.

### Risks

- A poorly-written Skill can corrupt chunks (bad span anchors,
  inconsistent markdown). Mitigation: a Skill conformance test suite
  every plugin passes; the citation validator catches malformed spans
  downstream.

## Alternatives considered

### Monolithic ingestion service with format branches

Cheaper short-term but turns into a swamp at five formats and is
unmaintainable at ten.

### Per-format microservices

More isolation but more operational cost. Distinct .NET assemblies
loaded into the ingestion worker give us code-level isolation
without the operational tax of a service per format. If a Skill
genuinely needs its own scaling profile, we can deploy it as a
separate Container App and dispatch via Service Bus.

## Skill backlog (v2 and beyond)

Selected as priority follow-up Skills based on enterprise content
patterns:

| Skill | Notes |
|---|---|
| `Skills.Html` | Web pages and clipped articles; Readability-style content extraction; preserve canonical URL and clipping date |
| `Skills.Email` | `.eml` and `.msg` files; thread-aware chunking; sender/recipient/subject metadata; attachment recursion through other Skills |
| `Skills.Csv` | Tabular data; per-row or per-column-block chunking; header-aware; supports CSV and Excel exports |
| `Skills.Json` | JSON / YAML config files; structure-aware chunking with key-path span anchors |
| `Skills.Diagrams` | Visio / draw.io / Lucidchart exports; structured shape + text extraction (in addition to image-vision fallback) |
| `Skills.Logs` | Structured application logs; pattern-aware chunking; time-range span anchors |
| `Skills.Tickets` | Azure DevOps work items, Jira issues, GitHub issues via API; thread-aware (work item + comments); first-class linkbacks |
| `Skills.WikiExport` | SharePoint pages, Confluence space exports, Notion exports; preserves the original site's hierarchy as metadata |

These are not committed to a phase; they are the prioritized backlog
when v1 is operational and we need to expand format coverage. Each
follows the standard plugin pattern — no core changes required.

## References

- [Synthadoc — SKILL.md manifest pattern](https://github.com/axoviq-ai/synthadoc)
- [Open Brain — recipes structure](https://github.com/NateBJones-Projects/OB1/tree/main/recipes)
- ADR 0007 — Claim-level citation contract (defines span anchors)
- ADR 0013 — Hyperscaler deployment scope (per-cloud adapters — e.g., `Skills.Pdf` targeting Azure AI Document Intelligence on Azure deployments and AWS Textract on AWS deployments — live inside the Skill plugin pattern; the plugin contract is the abstraction boundary)
