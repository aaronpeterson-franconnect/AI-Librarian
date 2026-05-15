# Skill: Markdown

mime_types:

- text/markdown
- text/x-markdown

extensions:

- .md
- .markdown

capability_tier: full

implementation: dotnet

external_services: []

default_chunk_strategy: paragraph-grouped

default_max_chunk_tokens: 500

span_format:

	type: object
	properties:
		type:
			type: string
			const: markdown
		paragraphIndex:
			type: integer
			minimum: 0

required_role: Contributor
