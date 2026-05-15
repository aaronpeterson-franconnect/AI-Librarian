using AiLibrarian.Domain.Personas;
using AiLibrarian.Infrastructure.Rls;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiLibrarian.Infrastructure.Retrieval;

/// <summary>
/// Decorator over <see cref="IHybridChunkSearch"/> that applies the
/// persona retrieval profile per ADR 0015. Sits between the API
/// retrieval surface and the raw <see cref="HybridChunkSearch"/> so
/// the rerank step is composable, swappable, and observable.
///
/// <para><b>Path:</b>
/// <list type="number">
///   <item>When <c>sessionContext.PersonaId</c> is null → delegate
///         directly to the inner search. No extra query, no rerank.</item>
///   <item>When set → over-fetch a multiple of the requested limit
///         from the inner search, load the persona profile, rerank,
///         and return the top <c>limit</c>. The over-fetch factor is
///         configurable; a profile that's effectively neutral (all
///         multipliers 1.0) will preserve the inner ordering.</item>
/// </list>
/// </para>
///
/// <para><b>Visibility-safe.</b> The decorator never modifies the inner
/// query, the RLS context, or the SQL predicate. RLS narrows the
/// authorized set; persona only reorders what came back.</para>
/// </summary>
public sealed class PersonaAwareHybridChunkSearch : IHybridChunkSearch
{
	private readonly IHybridChunkSearch _inner;
	private readonly IPersonaProfileReader _profileReader;
	private readonly IOptions<PersonaRetrievalOptions> _options;
	private readonly ILogger<PersonaAwareHybridChunkSearch> _logger;
	private readonly TimeProvider _timeProvider;

	/// <summary>
	/// Creates the decorator. The <paramref name="inner"/> dependency is
	/// typed as <see cref="IHybridChunkSearch"/> for testability; the
	/// production DI registration in
	/// <c>RetrievalServiceCollectionExtensions</c> hard-wires it to the
	/// concrete <see cref="HybridChunkSearch"/> to avoid a self-cycle.
	/// </summary>
	public PersonaAwareHybridChunkSearch(
		IHybridChunkSearch inner,
		IPersonaProfileReader profileReader,
		IOptions<PersonaRetrievalOptions> options,
		ILogger<PersonaAwareHybridChunkSearch> logger,
		TimeProvider? timeProvider = null)
	{
		_inner = inner;
		_profileReader = profileReader;
		_options = options;
		_logger = logger;
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<HybridChunkHit>> SearchAsync(
		RlsSessionContext sessionContext,
		string queryText,
		ReadOnlyMemory<float> queryEmbedding,
		HybridSearchRequestOptions options,
		CancellationToken cancellationToken)
	{
		// No persona set -- skip everything. Production "neutral" path.
		if (sessionContext.PersonaId is not Guid personaId || personaId == Guid.Empty)
		{
			return await _inner.SearchAsync(sessionContext, queryText, queryEmbedding, options, cancellationToken).ConfigureAwait(false);
		}

		var opts = _options.Value;
		var overFetchFactor = Math.Clamp(opts.OverFetchFactor, 1, 10);
		var overFetchLimit = Math.Min(options.Limit * overFetchFactor, opts.MaxOverFetchLimit);

		// Over-fetch from the inner search.
		var wider = await _inner
			.SearchAsync(
				sessionContext,
				queryText,
				queryEmbedding,
				options with { Limit = overFetchLimit },
				cancellationToken)
			.ConfigureAwait(false);

		PersonaRetrievalProfile profile;
		try
		{
			profile = await _profileReader.GetRetrievalProfileAsync(personaId, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// A profile-read failure must NOT poison retrieval. Fall back
			// to neutral and return the inner ordering (truncated to limit).
			_logger.LogWarning(ex,
				"PersonaAwareHybridChunkSearch: profile load failed for persona {PersonaId}; using neutral reranker.",
				personaId);
			profile = PersonaRetrievalProfile.Neutral;
		}

		var allCallerDeptIds = sessionContext.HomeDepartmentIds
			.Concat(sessionContext.ContributorDepartmentIds)
			.Concat(sessionContext.ReviewerDepartmentIds)
			.Concat(sessionContext.LibrarianDepartmentIds)
			.Distinct()
			.ToArray();

		var reranked = PersonaReranker.Rerank(
			wider,
			profile,
			callerDepartmentIds: allCallerDeptIds,
			now: _timeProvider.GetUtcNow(),
			limit: options.Limit);

		_logger.LogDebug(
			"Persona rerank persona={Persona} candidates={Candidates} returned={Returned}",
			personaId, wider.Count, reranked.Count);

		return reranked;
	}
}

/// <summary>Tuning for <see cref="PersonaAwareHybridChunkSearch"/>.</summary>
public sealed class PersonaRetrievalOptions
{
	/// <summary>Configuration section name.</summary>
	public const string SectionName = "PersonaRetrieval";

	/// <summary>How many times the requested limit to over-fetch before reranking. Default 3.</summary>
	public int OverFetchFactor { get; set; } = 3;

	/// <summary>Absolute cap on the over-fetched candidate count. Default 150 — keeps the wider fetch bounded even at large requested limits.</summary>
	public int MaxOverFetchLimit { get; set; } = 150;
}
