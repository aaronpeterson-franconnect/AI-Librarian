--liquibase formatted sql

--changeset ai-librarian:seed-personas-v1
--comment: The eight v1 personas per docs/personas/. Idempotent; safe to re-run.
--comment: Engineering is the v1 pilot; the others ship as defined-but-not-yet-piloted
--comment: rows so persona_memberships can be created during testing without
--comment: round-tripping through Admin tooling.
INSERT INTO personas (name, display_name, description, classification_floor) VALUES
	('engineering',		'Engineering',				'Triage client tickets against SRE conversations, code, SQL, and product wikis. v1 pilot persona.',	'Internal'),
	('product',			'Product',					'Coalesce customer signals, engineering feasibility, and competitive context to prioritize features.',	'Internal'),
	('sre',				'SRE / Operations',			'Investigate incidents, runbooks, and capacity using post-mortems and operational telemetry.',			'Internal'),
	('sales',			'Sales',					'Prepare account briefs, follow-ups, and proposal drafts grounded in product and account context.',	'Internal'),
	('marketing',		'Marketing',				'Author and review content grounded in approved messaging, claims, and brand directives.',				'Internal'),
	('customer-success','Customer Success',			'Renewals, QBRs, escalations grounded in account history, support patterns, and product roadmap.',		'Internal'),
	('legal-compliance','Legal / Compliance',		'Internal-only research, policy lookup, and contract redline drafting; high abstention discipline.',	'Confidential'),
	('hr-people',		'HR / People',				'People programs, policy lookup, anonymous-feedback synthesis with strict re-identification controls.','Confidential')
ON CONFLICT (name) DO NOTHING;
--rollback DELETE FROM personas WHERE name IN ('engineering','product','sre','sales','marketing','customer-success','legal-compliance','hr-people');
