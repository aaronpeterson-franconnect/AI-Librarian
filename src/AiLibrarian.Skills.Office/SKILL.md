# Skill: Office (DOCX / XLSX / PPTX)

Three sibling `ISkill` implementations under one assembly, registered
together by the ingest worker.

## docx

mime_types:

- application/vnd.openxmlformats-officedocument.wordprocessingml.document

extensions:

- .docx

capability_tier: full

implementation: dotnet — DocumentFormat.OpenXml

external_services: []

default_chunk_strategy: paragraph-grouped (heading-aware)

default_max_chunk_tokens: 500

span_format:

	type: object
	properties:
		type:
			type: string
			const: docx
		paragraphIndex:
			type: integer
			minimum: 0
		headingLevel:
			type: integer
			nullable: true

required_role: Contributor

## xlsx

mime_types:

- application/vnd.openxmlformats-officedocument.spreadsheetml.sheet

extensions:

- .xlsx

capability_tier: full

implementation: dotnet — DocumentFormat.OpenXml

external_services: []

default_chunk_strategy: per-sheet markdown table

default_max_chunk_tokens: 2000

span_format:

	type: object
	properties:
		type:
			type: string
			const: xlsx
		sheet:
			type: string
		sheetIndex:
			type: integer
			minimum: 0

required_role: Contributor

## pptx

mime_types:

- application/vnd.openxmlformats-officedocument.presentationml.presentation

extensions:

- .pptx

capability_tier: partial — slide body text only; speaker notes pending

implementation: dotnet — DocumentFormat.OpenXml

external_services: []

default_chunk_strategy: per-slide

default_max_chunk_tokens: 800

span_format:

	type: object
	properties:
		type:
			type: string
			const: pptx
		slide:
			type: integer
			minimum: 1

required_role: Contributor
