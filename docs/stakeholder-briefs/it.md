# IT Brief — AI Librarian

> Audience: IT Director, Identity / Entra administrator, Security architect
> Status: **Draft for review** · Date: 2026-04-29
> Companion documents: [Architecture](../architecture.md) · [Executive summary](../executive-summary.md)

## Why we need your input

AI Librarian's identity model is anchored on Microsoft Entra ID and
sits squarely in IT's domain. Two questions remain that need IT
guidance, both touching how non-employee identities and
non-interactive ingest paths plug into the system.

We're proposing concrete positions below and asking IT to ratify,
adjust, or reject.

A note on what's already settled: ADR 0005 is locked — five roles
(Reader, Contributor, Reviewer, Librarian, Admin), each
`(department, role)` mapped to one Entra group, group names
opaque to the system and following IT's existing naming
convention. The IT runbook for Phase 0 just needs you to publish
the canonical pattern; that's not a question, just a pre-work
item.

---

## Decision 1 — External / contractor access

### What the system does

ADR 0005's RLS distinguishes employees from B2B guests via the
`app.is_employee` session variable. **Employees** get classification-
driven read access: `Internal` content is readable across the
company, `Confidential` and `Restricted` content stays department-
scoped. **B2B guests** are explicitly *not* treated as company-wide
readers — they get strict per-department access, scoped to whichever
department `Reader` group they're added to, plus any explicit
`source_shares` granted to that department.

The question: do external collaborators (consultants, contractors,
partners, vendors) need to read or write to AI Librarian, and if
so how do they authenticate?

### Three patterns IT could choose from

| Pattern | What it looks like | Trade-off |
|---|---|---|
| **A. Employees only** | No external access. Externals never touch AI Librarian. | Simplest. Forces externals to consume knowledge through employees who relay it. |
| **B. B2B guests, read-only** | External collaborators get B2B guest accounts in Entra; can be added to a department's `Reader` group, never Contributor or above. | Standard pattern; well-supported. External can search, query, read citations. |
| **C. B2B guests with limited contribute rights** | Same as B, but a guest can also be added to `Contributor` (submits to approval queue, like any contributor) for specific projects. Never Reviewer / Librarian / Admin. | Maximum flexibility; biggest threat-model surface. Requires explicit guest-tagging in the audit ledger. |

### Our proposal

**Default to Pattern B (B2B guests, read-only) for v1.** Matches
common enterprise practice; minimal threat-model expansion. Pattern
C is achievable as a Phase 5 incremental change if a real use case
emerges.

**Auth tests must include**:

- B2B guest can be added to a `Reader` group and authenticate via
  the standard Entra OAuth flow
- B2B guest cannot be silently elevated to Contributor / Reviewer /
  Librarian (RLS predicates already prevent this; the auth tests
  prove it stays prevented)
- **B2B guest does not gain company-wide `Internal` read access** —
  they only see content in the specific department(s) they're
  granted on, plus shares to those departments
- B2B guest's audit events are tagged with their external identity
  for separate forensic review

### What we need from you

1. **Confirm Pattern B** is acceptable, or pick A (no externals) or
   C (externals as contributors).
2. **Confirm B2B guest provisioning workflow** — who approves
   adding a guest to an AI Librarian group? Standard IT request, or
   a separate librarian-approved flow?
3. **Specify any guest-account constraints**: do guests need to be
   re-validated periodically? Auto-removed after N days of inactivity?
4. **Identify any contractual obligations** that require AI
   Librarian access for specific external parties (e.g., an MSP
   that needs to read our infrastructure runbook).

### Phase relevance

**Phase 4** — external-access support is a Phase 4 deliverable
gated on this answer. Phases 0-3 operate employees-only.

---

## Decision 2 — Email-drop and connector authentication

### What the system does

Two ingest paths beyond the web portal need to authenticate the
submitter back to a real Entra identity:

1. **Email drop** — a librarian-curated email address (e.g.,
   `engineering-librarian@yourco.com`) that contributors can email
   documents to. The email subject/body becomes metadata; the
   attachment becomes a source.
2. **Connector flows** — periodic SharePoint, OneDrive, Confluence,
   GitHub-Enterprise pulls that ingest content from a known location
   on behalf of a department.

For both, the audit ledger needs to record *who* originated the
content, not just *what system* delivered it.

### Three options for email drop

| Option | What it looks like | Trade-off |
|---|---|---|
| **A. SMTP From-header trust** | Use the verified sender address (DKIM-validated) and look up the matching Entra user. | Fragile — header spoofing, distribution-list senders, etc. |
| **B. Outlook add-in / Power Automate flow** | User submits via a one-click button in Outlook that signs the message with their Entra identity before delivery. | Requires deploying an add-in or Power Automate flow tenant-wide. |
| **C. Forward-from-employee enforcement** | Reject any email whose verified `From` is not an employee in the tenant. Treat as Contributor for that department; submission lands in approval queue. | Pragmatic; rejects the easy spoofing cases without requiring a custom client. |

### Three options for connectors

| Option | What it looks like | Trade-off |
|---|---|---|
| **D. Service-principal as submitter** | The connector is a service principal; all ingested sources are credited to "Connector Service" in the audit ledger. | Loses per-user attribution; OK for legitimately shared sources. |
| **E. Last-modified-by attribution** | The connector preserves the original document's `last_modified_by` field and records that as the originator (looked up against Entra). | Best fidelity; depends on connector source supporting the field. |
| **F. Hybrid** | Use D for non-attributable sources (shared SharePoint folder); E where attribution is available. | Most pragmatic; matches reality. |

### Our proposal

- **Email drop**: Option C. Reject non-employee senders; treat
  verified employee senders as the submitting Contributor for the
  matching department. Phase 5 add-on (Q11 is already deferred).
- **Connectors**: Option F (hybrid). Service-principal default with
  last-modified-by attribution where available. Phase 5.

### What we need from you

1. **Confirm Option C for email drop** is acceptable, or pick A or
   B. (B requires Outlook add-in deployment; we want to know if
   you'd prefer that.)
2. **Confirm Option F for connectors** is acceptable, or pick D or
   E exclusively.
3. **Specify which connector sources** you want to enable in
   Phase 5 (SharePoint Online? OneDrive? Confluence? GitHub
   Enterprise? Other?).
4. **Specify the service-principal naming and lifecycle**: who
   owns the AI Librarian connector service principals, and how
   are they rotated?

### Phase relevance

**Phase 5** — both email drop and connectors are Phase 5
deliverables. We have time, but it helps to lock the auth pattern
now so the audit-ledger schema reserves the right fields.

---

## Summary — what we are asking IT to do

1. **External access pattern** (Q7): A, B, or C — default proposal
   B (B2B guests, read-only).
2. **B2B guest provisioning workflow** and lifecycle constraints.
3. **Email-drop auth pattern** (Q11): A, B, or C — default
   proposal C.
4. **Connector auth pattern** (Q11): D, E, or F — default proposal F.
5. **Phase 5 connector source list**: which platforms do you want
   onboarded (SharePoint, OneDrive, Confluence, GitHub Enterprise,
   etc.)?
6. **Service principal lifecycle**: ownership, rotation, audit.
7. **Pre-work for Phase 0**: publish the canonical Entra group
   naming pattern for AI Librarian groups (already agreed in Q5;
   just needs the actual string).
8. **Confirm enterprise-tier subscriptions for AI tools** (per
   [ADR 0012](../adr/0012-enterprise-tier-llm-access.md)). AI
   Librarian's privacy posture rests on every LLM endpoint —
   server-side providers and client-side AI tools — operating under
   an enterprise-tier agreement with no-training and bounded-
   retention data-handling guarantees. Specifically, please confirm:
	- **Server-side**: Azure OpenAI is provisioned in our tenant
	  under the standard Microsoft Customer Agreement (this is the
	  default and likely already true; just need the confirmation).
	- **Client-side AI tool licenses**: which of the following do
	  we hold enterprise / business tiers for, and which need
	  upgrading? Microsoft Copilot (M365), GitHub Copilot,
	  Cursor (Business / Enterprise), Claude (Work / Team /
	  Enterprise), ChatGPT (Enterprise / Business), Teams (M365).
	- **Data handling addenda filed**: where applicable (Azure
	  OpenAI abuse-monitoring opt-out; OpenAI training opt-out
	  confirmation; Cursor Privacy Mode at the org level), please
	  confirm these are in place.
	- **Living approved-list document**: IT and Security own the
	  contents of `docs/llm-providers.md` once Phase 0 ships;
	  please nominate an owner from each function.

## Where to read more

- [`../architecture.md`](../architecture.md) — full system blueprint
- [`../adr/0005-rls-with-entra.md`](../adr/0005-rls-with-entra.md) — identity & authorization
- [`../adr/0010-audit-ledger.md`](../adr/0010-audit-ledger.md) — audit ledger
- [`../open-questions.md`](../open-questions.md) — full open-questions tracker
