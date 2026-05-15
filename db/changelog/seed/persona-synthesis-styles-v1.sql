--liquibase formatted sql

--changeset ai-librarian:seed-persona-synthesis-styles-v1
--comment: ADR 0015 synthesis styles. Pairs with the retrieval-profile
--comment: seed -- same idempotent UPDATE pattern, same Engineering-pilot
--comment: scope. Re-tune by editing this file and re-running Liquibase.
--comment:
--comment: Engineering pilot style (per docs/personas/engineering.md +
--comment: ADR 0015 §"Synthesis-style shape"):
--comment:   - answerLengthHint = medium -- enough to cover the topic
--comment:     without padding; engineering readers skim.
--comment:   - structurePreference = narrative -- runbooks need prose
--comment:     for the why + bullets for the how; the Wiki Maintainer
--comment:     already permits markdown headings so the LLM can mix
--comment:     within "narrative".
--comment:   - citationDensity = per-claim -- matches the structural
--comment:     contract; engineering values traceability strongly.
--comment:   - codeQuoting = preserve-context -- function snippets that
--comment:     include the caller / the surrounding control flow are
--comment:     more useful than isolated lines.
--comment:   - hedgingPosture = calibrated -- the default; engineering
--comment:     wants to know when the sources are thin.
--comment:   - abstentionThreshold = 0.75 -- slightly stricter than the
--comment:     0.7 neutral default; engineering work prefers "I don't
--comment:     know" over a low-confidence guess.
--comment:   - crossSourceSynthesis = always -- blend runbook + ticket
--comment:     + code when they agree, so the page covers the whole
--comment:     surface.
--comment:   - showSourceMetadata = true -- visible source attribution
--comment:     in the prose helps reviewers spot stale chunks.
UPDATE personas
SET synthesis_style = '{
	"answerLengthHint": "medium",
	"structurePreference": "narrative",
	"citationDensity": "per-claim",
	"codeQuoting": "preserve-context",
	"hedgingPosture": "calibrated",
	"abstentionThreshold": 0.75,
	"crossSourceSynthesis": "always",
	"showSourceMetadata": true
}'::jsonb
WHERE name = 'engineering';
--rollback UPDATE personas SET synthesis_style = '{}'::jsonb WHERE name = 'engineering';
