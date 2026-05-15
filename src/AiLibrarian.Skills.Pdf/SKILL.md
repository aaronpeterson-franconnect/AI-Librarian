# Skill: PDF

Per ADR 0009. Pure .NET extractor backed by
[UglyToad.PdfPig](https://github.com/UglyToad/PdfPig) (MIT-licensed).

## pdf

mime_types:

- application/pdf

extensions:

- .pdf

capability_tier: full

implementation: dotnet — UglyToad.PdfPig

external_services: []

default_chunk_strategy: page-grouped

default_max_chunk_tokens: 800

span_format:

	type: object
	properties:
		type:
			const: pdf
		pageNumber:
			type: integer
			description: 1-indexed page number this chunk came from.

## Known limitations

- **No OCR.** PdfPig reads embedded text; scanned PDFs with rasterized
  pages produce empty chunks. The skill emits a `pdf.no_text` issue so
  the operator sees why retrieval can't find them. Phase 4 plan: an
  OCR companion skill that the registry routes to when this one
  returns no text.
- **Encrypted PDFs** without an empty-password decrypt path produce a
  `pdf.encrypted` issue and an empty result. Owner-password decryption
  is out of scope for Phase 1.
- **Tables** are linearized to text. Faithful table reconstruction in
  PDFs is a separate research problem; the Phase 2 wiki maintainer
  may surface table-shaped facets that need richer skill output.

## Implementation notes

- Page text is extracted via `ContentOrderTextExtractor.GetText(page)`
  which preserves reading order better than `page.Text`.
- Page boundaries become chunk boundaries — one chunk per page. The
  ingest pipeline's downstream chunker may further split, but the
  canonical mapping is "page → chunk."
- Whitespace is normalized: runs of spaces collapse to one, blank
  lines collapse to two. Hard line breaks inside paragraphs (PDF text
  layouts often wrap mid-sentence) are joined.
