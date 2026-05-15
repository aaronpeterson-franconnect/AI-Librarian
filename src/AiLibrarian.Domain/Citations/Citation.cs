namespace AiLibrarian.Domain.Citations;

/// <summary>
/// One pointer from a <see cref="Claim"/> back into a
/// <c>source_chunks</c> row. ADR 0007 names the contract: every claim
/// resolves to one or more concrete spans in the corpus, and every
/// citation must survive the five validation rules in
/// <see cref="CitationRule"/>.
/// </summary>
/// <param name="Id">Stable identifier for the citation row.</param>
/// <param name="ChunkId">The <c>source_chunks.id</c> being cited.</param>
/// <param name="SpanStart">
/// Inclusive character offset into the canonicalized chunk text. Rule 3
/// requires <c>0 &lt;= SpanStart &lt; SpanEnd &lt;= chunk.length</c>.
/// </param>
/// <param name="SpanEnd">
/// Exclusive character offset. See <see cref="SpanStart"/>.
/// </param>
/// <param name="Confidence">
/// Synthesis-time confidence in [0, 1]. Rule 5 fails the citation when
/// it falls below the configured floor (default 0.7).
/// </param>
public sealed record Citation(
	Guid Id,
	Guid ChunkId,
	int SpanStart,
	int SpanEnd,
	double Confidence);
