--liquibase formatted sql

--changeset ai-librarian:seed-persona-retrieval-profiles-v1
--comment: ADR 0015 retrieval profiles. v1 ships a non-neutral profile for the
--comment: Engineering pilot persona; everyone else stays on the empty default.
--comment: Idempotent: UPDATE overwrites whatever's there, so re-running the
--comment: seed swaps in a refined profile without DDL churn. Re-tune by
--comment: editing this file and re-running Liquibase.
--comment:
--comment: Engineering profile rationale (per docs/personas/engineering.md +
--comment: ADR 0015 §"Retrieval-profile shape"):
--comment:   - recencyHalfLifeDays = 90 -- engineering work skews to recent
--comment:     runbook updates, ticket threads, and code changes; ~3-month
--comment:     half-life keeps year-old content from dominating.
--comment:   - authorityBias: current = 1.4 (approved sources), draft = 0.6
--comment:     (unapproved) -- engineering pilots strongly prefer the
--comment:     librarian-approved version of a runbook over the work-in-progress.
--comment:   - crossDepartmentBoost: sameDepartment = 1.3 (an Engineering
--comment:     contributor's own dept material is preferred), cross-Internal
--comment:     = 1.0 (no opinion), cross-shared (Confidential+) = 1.1 (when
--comment:     an SRE shares a Confidential incident review, we want the
--comment:     engineer to see it slightly boosted).
--comment:
--comment: sourceTypeWeights is now ACTIVE against sources.source_type
--comment: (migration 0028). Hits whose source_type is NULL (pre-classifier
--comment: backfill) get no weight applied -- the dimension only fires on
--comment: classified rows.
UPDATE personas
SET retrieval_profile = '{
	"sourceTypeWeights": {
		"code": 1.5,
		"sql": 1.3,
		"runbook": 1.4,
		"ticket": 1.2,
		"meeting_transcript": 0.9,
		"wiki_page": 1.0,
		"email": 0.7,
		"image": 0.6
	},
	"recencyHalfLifeDays": 90,
	"authorityBias": {
		"current": 1.4,
		"draft": 0.6,
		"canonical": 1.5,
		"superseded": 0.3
	},
	"crossDepartmentBoost": {
		"sameDepartment": 1.3,
		"crossDepartmentInternal": 1.0,
		"crossDepartmentShared": 1.1
	},
	"floorClassification": "Internal"
}'::jsonb
WHERE name = 'engineering';
--rollback UPDATE personas SET retrieval_profile = '{}'::jsonb WHERE name = 'engineering';
