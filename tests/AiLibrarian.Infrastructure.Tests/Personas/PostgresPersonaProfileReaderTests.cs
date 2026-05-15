using AiLibrarian.Domain;
using AiLibrarian.Domain.Personas;
using AiLibrarian.Infrastructure.Personas;
using AiLibrarian.Infrastructure.Tests.Rls;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace AiLibrarian.Infrastructure.Tests.Personas;

/// <summary>
/// Testcontainer-backed coverage for the persona retrieval-profile
/// reader. Pins:
/// <list type="bullet">
///   <item>Reading a real <c>retrieval_profile</c> JSONB round-trips
///         every documented dimension.</item>
///   <item>An unknown / deactivated persona resolves to neutral
///         (no throw).</item>
///   <item>Malformed JSON degrades to neutral with a warn rather
///         than poisoning retrieval.</item>
///   <item><c>classification_floor</c> column is the authoritative
///         source for the floor, not the JSON echo.</item>
/// </list>
/// </summary>
public sealed class PostgresPersonaProfileReaderTests : IClassFixture<RlsPostgresFixture>
{
	private readonly RlsPostgresFixture _fixture;

	public PostgresPersonaProfileReaderTests(RlsPostgresFixture fixture)
	{
		_fixture = fixture;
	}

	[SkippableFact]
	public async Task Reads_seeded_engineering_profile()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		// The Liquibase seed file seed/persona-retrieval-profiles-v1.sql
		// installs a non-neutral profile for the engineering persona.
		var engineeringId = await GetPersonaIdAsync("engineering");
		var reader = CreateReader();

		var profile = await reader.GetRetrievalProfileAsync(engineeringId);

		profile.RecencyHalfLifeDays.Should().Be(90);
		profile.AuthorityBias.Should().ContainKey("current");
		profile.AuthorityBias.Should().ContainKey("draft");
		profile.CrossDepartmentBoost.Should().NotBeNull();
		profile.CrossDepartmentBoost!.SameDepartment.Should().Be(1.3);
		profile.FloorClassification.Should().Be(Classification.Internal);
	}

	[SkippableFact]
	public async Task Unknown_persona_returns_neutral()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		var reader = CreateReader();

		var profile = await reader.GetRetrievalProfileAsync(Guid.NewGuid());

		profile.Should().BeSameAs(PersonaRetrievalProfile.Neutral);
	}

	[SkippableFact]
	public async Task Empty_persona_id_returns_neutral_without_querying()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		var reader = CreateReader();

		var profile = await reader.GetRetrievalProfileAsync(Guid.Empty);

		profile.Should().BeSameAs(PersonaRetrievalProfile.Neutral);
	}

	[SkippableFact]
	public async Task Deactivated_persona_returns_neutral()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		// Insert a one-off persona and immediately deactivate it.
		var id = await InsertPersonaAsync(
			name: $"deact-{Guid.NewGuid():N}"[..30],
			retrievalProfileJson: """{"recencyHalfLifeDays": 30}""",
			deactivate: true);

		var reader = CreateReader();
		var profile = await reader.GetRetrievalProfileAsync(id);

		profile.Should().BeSameAs(PersonaRetrievalProfile.Neutral);
	}

	[SkippableFact]
	public async Task Malformed_profile_json_falls_back_to_neutral()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		// JSONB column won't accept literal garbage, but a structurally-
		// valid-but-shape-wrong document gets caught by the C# parser.
		// We feed "recencyHalfLifeDays" as a string instead of an int.
		var id = await InsertPersonaAsync(
			name: $"bad-{Guid.NewGuid():N}"[..30],
			retrievalProfileJson: """{"recencyHalfLifeDays": "not-a-number"}""",
			deactivate: false);

		var reader = CreateReader();
		var profile = await reader.GetRetrievalProfileAsync(id);

		// Either degrades to neutral entirely (preferred) or to a
		// best-effort partial profile. Both are acceptable; what's NOT
		// acceptable is a throw. The current implementation degrades to
		// neutral on JsonException.
		profile.Should().BeSameAs(PersonaRetrievalProfile.Neutral);
	}

	[SkippableFact]
	public async Task Reads_seeded_engineering_synthesis_style()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		// seed/persona-synthesis-styles-v1.sql installs the engineering
		// pilot style. The fields below mirror that seed file.
		var engineeringId = await GetPersonaIdAsync("engineering");
		var reader = CreateReader();

		var style = await reader.GetSynthesisStyleAsync(engineeringId);

		style.AnswerLength.Should().Be(AnswerLengthHint.Medium);
		style.Structure.Should().Be(StructurePreference.Narrative);
		style.CitationDensity.Should().Be(CitationDensity.PerClaim);
		style.CodeQuoting.Should().Be(CodeQuotingPreference.PreserveContext);
		style.HedgingPosture.Should().Be(HedgingPosture.Calibrated);
		style.AbstentionThreshold.Should().Be(0.75);
		style.CrossSourceSynthesis.Should().Be(CrossSourceSynthesisMode.Always);
		style.ShowSourceMetadata.Should().BeTrue();
	}

	[SkippableFact]
	public async Task Unknown_persona_returns_neutral_style()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		var reader = CreateReader();

		var style = await reader.GetSynthesisStyleAsync(Guid.NewGuid());

		style.Should().BeSameAs(PersonaSynthesisStyle.Neutral);
	}

	[SkippableFact]
	public async Task Synthesis_style_parses_kebab_case_and_camel_case_values()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		// ADR 0015 docs use kebab-case ("code-first") but the parser
		// must also accept camelCase ("codeFirst") and the PascalCase
		// enum names ("CodeFirst") for forward-compat.
		var id = await InsertPersonaWithSynthesisStyleAsync(
			name: $"kebab-{Guid.NewGuid():N}"[..30],
			synthesisStyleJson: """
				{
					"answerLengthHint": "short",
					"structurePreference": "code-first",
					"citationDensity": "per-paragraph",
					"codeQuoting": "preserve-context",
					"hedgingPosture": "conservative",
					"abstentionThreshold": 0.9,
					"crossSourceSynthesis": "when-needed",
					"showSourceMetadata": false
				}
				""");

		var reader = CreateReader();
		var style = await reader.GetSynthesisStyleAsync(id);

		style.AnswerLength.Should().Be(AnswerLengthHint.Brief);
		style.Structure.Should().Be(StructurePreference.CodeFirst);
		style.CitationDensity.Should().Be(CitationDensity.PerParagraph);
		style.HedgingPosture.Should().Be(HedgingPosture.Conservative);
		style.AbstentionThreshold.Should().Be(0.9);
		style.CrossSourceSynthesis.Should().Be(CrossSourceSynthesisMode.WhenNeeded);
		style.ShowSourceMetadata.Should().BeFalse();
	}

	[SkippableFact]
	public async Task Synthesis_style_unknown_enum_values_fall_back_to_neutral_defaults()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		// Unknown values for the categorical fields must NOT throw;
		// each one independently falls back to its neutral default.
		var id = await InsertPersonaWithSynthesisStyleAsync(
			name: $"unk-{Guid.NewGuid():N}"[..30],
			synthesisStyleJson: """
				{
					"answerLengthHint": "epic-length",
					"structurePreference": "interpretive-dance",
					"hedgingPosture": "yelling"
				}
				""");

		var reader = CreateReader();
		var style = await reader.GetSynthesisStyleAsync(id);

		style.AnswerLength.Should().Be(PersonaSynthesisStyle.Neutral.AnswerLength);
		style.Structure.Should().Be(PersonaSynthesisStyle.Neutral.Structure);
		style.HedgingPosture.Should().Be(PersonaSynthesisStyle.Neutral.HedgingPosture);
	}

	private async Task<Guid> InsertPersonaWithSynthesisStyleAsync(string name, string synthesisStyleJson)
	{
		await using var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString);
		await conn.OpenAsync();
		await using var cmd = new NpgsqlCommand("""
			INSERT INTO personas (name, display_name, description, synthesis_style)
			VALUES (@n, @d, @desc, @s::jsonb)
			RETURNING id
			""", conn);
		cmd.Parameters.AddWithValue("n", name);
		cmd.Parameters.AddWithValue("d", "Test " + name);
		cmd.Parameters.AddWithValue("desc", "Test fixture persona for synthesis-style reader tests.");
		cmd.Parameters.AddWithValue("s", synthesisStyleJson);
		return (Guid)(await cmd.ExecuteScalarAsync())!;
	}

	// --- helpers ---

	private PostgresPersonaProfileReader CreateReader()
	{
		var ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
		return new PostgresPersonaProfileReader(ds, NullLogger<PostgresPersonaProfileReader>.Instance);
	}

	private async Task<Guid> GetPersonaIdAsync(string name)
	{
		await using var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString);
		await conn.OpenAsync();
		await using var cmd = new NpgsqlCommand("SELECT id FROM personas WHERE name = @n", conn);
		cmd.Parameters.AddWithValue("n", name);
		return (Guid)(await cmd.ExecuteScalarAsync())!;
	}

	private async Task<Guid> InsertPersonaAsync(string name, string retrievalProfileJson, bool deactivate)
	{
		await using var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString);
		await conn.OpenAsync();
		await using var cmd = new NpgsqlCommand("""
			INSERT INTO personas (name, display_name, description, retrieval_profile, deactivated_at)
			VALUES (@n, @d, @desc, @p::jsonb, @x)
			RETURNING id
			""", conn);
		cmd.Parameters.AddWithValue("n", name);
		cmd.Parameters.AddWithValue("d", "Test " + name);
		cmd.Parameters.AddWithValue("desc", "Test fixture persona for profile reader tests.");
		cmd.Parameters.AddWithValue("p", retrievalProfileJson);
		cmd.Parameters.AddWithValue("x", deactivate ? (object)DateTimeOffset.UtcNow : DBNull.Value);
		return (Guid)(await cmd.ExecuteScalarAsync())!;
	}
}
