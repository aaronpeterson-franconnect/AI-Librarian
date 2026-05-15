using System.Runtime.CompilerServices;

using AiLibrarian.Domain;
using AiLibrarian.Domain.Citations;
using AiLibrarian.Domain.Wiki;
using AiLibrarian.LlmGateway.Abstractions;
using AiLibrarian.Quality;
using AiLibrarian.WikiMaintainer;

namespace AiLibrarian.WikiMaintainer.Tests;

/// <summary>
/// Orchestrator tests with stubs at every collaborator: stub
/// IChatProvider returns canned Pass 1 prose; the real
/// CitationValidator + in-memory IChunkLookup do their thing; stub
/// IWikiRevisionWriter records what was committed.
///
/// <para>The point is to pin the orchestration contract: when does
/// the Maintainer admit a revision? When does it reject? What does
/// each rejection reason carry? Not "is the LLM's prose good" — that's
/// the eval harness's job.</para>
/// </summary>
public sealed class WikiMaintainerTests
{
	private static readonly Guid PageId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
	private static readonly Guid ChunkA = Guid.Parse("11111111-1111-1111-1111-111111111111");
	private static readonly Guid ChunkB = Guid.Parse("22222222-2222-2222-2222-222222222222");

	[Fact]
	public async Task Happy_Path_Commits_Revision_With_Two_Claims()
	{
		var chunks = new[]
		{
			new ChunkRef(ChunkA, Guid.NewGuid(), Classification.Internal, 600, false),
			new ChunkRef(ChunkB, Guid.NewGuid(), Classification.Internal, 600, false),
		};
		var llm = new StubChatProvider(
			$"First fact. [chunk:{ChunkA:D}] Second fact. [chunk:{ChunkB:D}]");
		var validator = MakeValidator(chunks);
		var writer = new RecordingWriter();
		var maintainer = MakeMaintainer(llm, validator, writer);

		var request = MakeRequest(chunks);
		var result = await maintainer.GenerateRevisionAsync(request);

		result.Succeeded.Should().BeTrue();
		result.RevisionId.Should().NotBeNull();
		result.ClaimCount.Should().Be(2);
		result.CitationCount.Should().Be(2);
		writer.Commits.Should().ContainSingle();
		writer.Commits[0].Claims.Should().HaveCount(2);
	}

	[Fact]
	public async Task Empty_Source_Pool_Rejects_Without_Calling_Llm()
	{
		var llm = new StubChatProvider("should not be called");
		var validator = MakeValidator(Array.Empty<ChunkRef>());
		var writer = new RecordingWriter();
		var maintainer = MakeMaintainer(llm, validator, writer);

		var request = MakeRequest(Array.Empty<ChunkRef>());
		var result = await maintainer.GenerateRevisionAsync(request);

		result.Succeeded.Should().BeFalse();
		result.RejectionReason.Should().Contain("No source chunks");
		llm.CallCount.Should().Be(0);
		writer.Commits.Should().BeEmpty();
	}

	[Fact]
	public async Task Empty_Llm_Output_Rejects_Before_Validation()
	{
		var chunks = new[] { new ChunkRef(ChunkA, Guid.NewGuid(), Classification.Internal, 600, false) };
		var llm = new StubChatProvider(string.Empty);
		var validator = MakeValidator(chunks);
		var writer = new RecordingWriter();
		var maintainer = MakeMaintainer(llm, validator, writer);

		var result = await maintainer.GenerateRevisionAsync(MakeRequest(chunks));

		result.Succeeded.Should().BeFalse();
		result.RejectionReason.Should().Contain("empty prose");
		writer.Commits.Should().BeEmpty();
	}

	[Fact]
	public async Task Pass2_Producing_No_Claims_Rejects()
	{
		// LLM returns text with no sentence-terminator + no citation
		// tokens -- Pass 2 will see one segment with no citations.
		// Actually, even single-segment-with-text counts as one claim
		// (empty-citation-claim). To get zero claims, return pure
		// whitespace.
		var chunks = new[] { new ChunkRef(ChunkA, Guid.NewGuid(), Classification.Internal, 600, false) };
		var llm = new StubChatProvider("   \n   ");
		var validator = MakeValidator(chunks);
		var writer = new RecordingWriter();
		var maintainer = MakeMaintainer(llm, validator, writer);

		var result = await maintainer.GenerateRevisionAsync(MakeRequest(chunks));

		// Whitespace-only prose -> "empty prose" rejection (the
		// IsNullOrWhiteSpace check trips first).
		result.Succeeded.Should().BeFalse();
		result.RejectionReason.Should().NotBeNull();
		writer.Commits.Should().BeEmpty();
	}

	[Fact]
	public async Task Claim_Without_Citation_Triggers_R1_Rejection()
	{
		// LLM forgot to add a [chunk:...] token. Pass 2 emits one
		// citation-less claim; the validator's rule 1 rejects.
		var chunks = new[] { new ChunkRef(ChunkA, Guid.NewGuid(), Classification.Internal, 600, false) };
		var llm = new StubChatProvider("This claim has no citation.");
		var validator = MakeValidator(chunks);
		var writer = new RecordingWriter();
		var maintainer = MakeMaintainer(llm, validator, writer);

		var result = await maintainer.GenerateRevisionAsync(MakeRequest(chunks));

		result.Succeeded.Should().BeFalse();
		result.ValidationResult.Violations.Should().Contain(v => v.Rule == CitationRule.ClaimHasCitation);
		result.RejectionReason.Should().Contain("R1.ClaimHasCitation");
		writer.Commits.Should().BeEmpty();
	}

	[Fact]
	public async Task Citation_Leaking_Confidential_Into_Internal_Triggers_R4_Rejection()
	{
		// The chunk is Confidential but the facet is Internal. Rule 4
		// (no leakage) must fire.
		var chunks = new[]
		{
			new ChunkRef(ChunkA, Guid.NewGuid(), Classification.Confidential, 600, false),
		};
		var llm = new StubChatProvider($"Leaky claim. [chunk:{ChunkA:D}]");
		var validator = MakeValidator(chunks);
		var writer = new RecordingWriter();
		var maintainer = MakeMaintainer(llm, validator, writer);

		var result = await maintainer.GenerateRevisionAsync(MakeRequest(chunks, facet: Classification.Internal));

		result.Succeeded.Should().BeFalse();
		result.ValidationResult.Violations.Should().Contain(v => v.Rule == CitationRule.ClassificationNotLeaking);
		result.RejectionReason.Should().Contain("R4");
		writer.Commits.Should().BeEmpty();
	}

	[Fact]
	public async Task Commit_Failure_Surfaces_Cleanly()
	{
		var chunks = new[] { new ChunkRef(ChunkA, Guid.NewGuid(), Classification.Internal, 600, false) };
		var llm = new StubChatProvider($"Valid claim. [chunk:{ChunkA:D}]");
		var validator = MakeValidator(chunks);
		var writer = new RecordingWriter { ThrowOnCommit = true };
		var maintainer = MakeMaintainer(llm, validator, writer);

		var result = await maintainer.GenerateRevisionAsync(MakeRequest(chunks));

		result.Succeeded.Should().BeFalse();
		result.RejectionReason.Should().Contain("commit failed");
		result.ValidationResult.IsValid.Should().BeTrue("validation passed; only the commit failed");
	}

	[Fact]
	public async Task Pass1_System_Prompt_Names_The_Facet_Classification_Ceiling()
	{
		var chunks = new[] { new ChunkRef(ChunkA, Guid.NewGuid(), Classification.Internal, 600, false) };
		var llm = new StubChatProvider($"Claim. [chunk:{ChunkA:D}]");
		var validator = MakeValidator(chunks);
		var writer = new RecordingWriter();
		var maintainer = MakeMaintainer(llm, validator, writer);

		await maintainer.GenerateRevisionAsync(MakeRequest(chunks, facet: Classification.Confidential));

		llm.CallCount.Should().Be(1);
		llm.LastSystemPrompt.Should().Contain("Confidential");
		llm.LastUserPrompt.Should().Contain($"[chunk:{ChunkA:D}]");
	}

	// ---------- helpers ----------

	private static WikiMaintainer MakeMaintainer(
		IChatProvider chat,
		ICitationValidator validator,
		IWikiRevisionWriter writer)
		=> new(chat, validator, writer);

	private static CitationValidator MakeValidator(IReadOnlyList<ChunkRef> pool)
	{
		var lookup = new DictionaryChunkLookup(pool);
		return new CitationValidator(lookup);
	}

	private static WikiMaintenanceRequest MakeRequest(
		IReadOnlyList<ChunkRef> pool,
		Classification facet = Classification.Internal)
	{
		var sourceChunks = pool
			.Select(c => new WikiMaintenanceSourceChunk(c.Id, new string('s', c.ContentLength), c.Classification))
			.ToList();

		return new WikiMaintenanceRequest(
			PageId: PageId,
			FacetClassification: facet,
			PersonaId: null,
			RevisionNumber: 1,
			Topic: "How the worker boots.",
			SourceChunks: sourceChunks,
			AuthoredBy: Guid.Parse("00000000-0000-0000-0000-00000000ffff"));
	}

	private sealed class StubChatProvider : IChatProvider
	{
		private readonly string _response;

		public string ProviderId => "stub";
		public int CallCount { get; private set; }
		public string? LastSystemPrompt { get; private set; }
		public string? LastUserPrompt { get; private set; }

		public StubChatProvider(string response)
		{
			_response = response;
		}

		public async IAsyncEnumerable<ChatCompletionChunk> StreamCompletionAsync(
			ChatCompletionRequest request,
			[EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			CallCount++;
			LastSystemPrompt = request.Messages.FirstOrDefault(m => m.Role == "system")?.Content;
			LastUserPrompt = request.Messages.FirstOrDefault(m => m.Role == "user")?.Content;
			await Task.Yield();
			yield return new ChatCompletionChunk(_response, "stop");
		}
	}

	private sealed class DictionaryChunkLookup : IChunkLookup
	{
		private readonly Dictionary<Guid, ChunkRef> _by;
		public DictionaryChunkLookup(IReadOnlyList<ChunkRef> chunks)
		{
			_by = chunks.ToDictionary(c => c.Id);
		}

		public Task<IReadOnlyDictionary<Guid, ChunkRef>> ResolveAsync(
			IReadOnlyCollection<Guid> chunkIds,
			CancellationToken cancellationToken = default)
		{
			var hits = chunkIds
				.Where(_by.ContainsKey)
				.ToDictionary(id => id, id => _by[id]);
			return Task.FromResult<IReadOnlyDictionary<Guid, ChunkRef>>(hits);
		}
	}

	private sealed class RecordingWriter : IWikiRevisionWriter
	{
		public List<WikiRevisionDraft> Commits { get; } = new();
		public bool ThrowOnCommit { get; set; }

		public Task<Guid> CommitAsync(WikiRevisionDraft draft, CancellationToken cancellationToken = default)
		{
			if (ThrowOnCommit)
			{
				throw new InvalidOperationException("simulated commit failure");
			}

			Commits.Add(draft);
			return Task.FromResult(Guid.NewGuid());
		}
	}

	[Fact]
	public async Task Locked_Page_Routes_To_Proposal_Writer_Instead_Of_Committing()
	{
		var chunks = new[] { new ChunkRef(ChunkA, Guid.NewGuid(), Classification.Internal, 600, false) };
		var llm = new StubChatProvider($"Valid claim. [chunk:{ChunkA:D}]");
		var validator = MakeValidator(chunks);
		var revisionWriter = new RecordingWriter();
		var proposalWriter = new RecordingProposalWriter();
		var pageReader = new StubPageReader { Locked = true };

		var maintainer = new WikiMaintainer(
			llm,
			validator,
			revisionWriter,
			options: null,
			logger: null,
			extractor: null,
			proposalWriter: proposalWriter,
			pageReader: pageReader);

		var result = await maintainer.GenerateRevisionAsync(MakeRequest(chunks));

		// The maintainer rejects-but-queues. Succeeded=false because no
		// revision committed; the proposal writer captured the payload.
		result.Succeeded.Should().BeFalse();
		result.RevisionId.Should().BeNull();
		result.RejectionReason.Should().Contain("Page is locked");
		result.RejectionReason.Should().Contain("proposal queued");

		revisionWriter.Commits.Should().BeEmpty("locked page routes to proposal, not revision");
		proposalWriter.Created.Should().ContainSingle();
		proposalWriter.Created[0].State.Should().Be(ProposalState.Pending);
		proposalWriter.Created[0].Payload.Claims.Should().ContainSingle();
		proposalWriter.Created[0].ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow.AddDays(13));
	}

	[Fact]
	public async Task Unlocked_Page_Commits_Normally_When_Lock_Path_Is_Wired()
	{
		// Same wiring as the locked test but Locked=false. The
		// maintainer must commit directly.
		var chunks = new[] { new ChunkRef(ChunkA, Guid.NewGuid(), Classification.Internal, 600, false) };
		var llm = new StubChatProvider($"Valid claim. [chunk:{ChunkA:D}]");
		var validator = MakeValidator(chunks);
		var revisionWriter = new RecordingWriter();
		var proposalWriter = new RecordingProposalWriter();
		var pageReader = new StubPageReader { Locked = false };

		var maintainer = new WikiMaintainer(
			llm,
			validator,
			revisionWriter,
			options: null,
			logger: null,
			extractor: null,
			proposalWriter: proposalWriter,
			pageReader: pageReader);

		var result = await maintainer.GenerateRevisionAsync(MakeRequest(chunks));

		result.Succeeded.Should().BeTrue();
		revisionWriter.Commits.Should().ContainSingle();
		proposalWriter.Created.Should().BeEmpty();
	}

	private sealed class StubPageReader : IWikiPageReader
	{
		public bool Locked { get; set; }
		public Task<bool> IsLockedAsync(Guid pageId, CancellationToken cancellationToken = default)
			=> Task.FromResult(Locked);

		public Task<IReadOnlySet<string>> ListSlugsAsync(Guid departmentId, CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
	}

	private sealed class RecordingProposalWriter : IWikiProposalWriter
	{
		public List<WikiProposedRevision> Created { get; } = new();

		public Task<Guid> CreateAsync(WikiProposedRevision proposal, CancellationToken cancellationToken = default)
		{
			Created.Add(proposal);
			return Task.FromResult(proposal.Id);
		}

		public Task<Guid?> DecideAsync(Guid proposalId, ProposalState decision, Guid decidedBy, string? reason, CancellationToken cancellationToken = default)
			=> Task.FromResult<Guid?>(null);

		public Task<int> ExpirePendingAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult(0);

		public Task<BulkRejectOutcome> BulkRejectAsync(
			IReadOnlyCollection<Guid> proposalIds,
			Guid decidedBy,
			string reason,
			CancellationToken cancellationToken = default)
			=> Task.FromResult(new BulkRejectOutcome(
				Array.Empty<Guid>(),
				Array.Empty<Guid>(),
				Array.Empty<Guid>()));
	}
}
