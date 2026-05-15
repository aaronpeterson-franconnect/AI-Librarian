using System.Text.Json;
using System.Text.Json.Serialization;

using AiLibrarian.Domain;
using AiLibrarian.Domain.Personas;
using AiLibrarian.Infrastructure.Rls;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace AiLibrarian.Infrastructure.Personas;

/// <summary>
/// Postgres-backed <see cref="IPersonaProfileReader"/>. Reads the
/// <c>personas.retrieval_profile</c> JSONB column under the system
/// admin RLS context (persona definitions are not caller-scoped) and
/// projects it into <see cref="PersonaRetrievalProfile"/>.
///
/// <para>Tolerant parser: unrecognised JSON keys are ignored,
/// missing required fields fall through to the neutral defaults,
/// and a malformed document logs at warn and returns
/// <see cref="PersonaRetrievalProfile.Neutral"/> rather than throwing.
/// A persona-system glitch must not 500 the retrieval path.</para>
/// </summary>
public sealed class PostgresPersonaProfileReader : IPersonaProfileReader
{
	private static readonly JsonSerializerOptions _jsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	private readonly NpgsqlDataSource _dataSource;
	private readonly ILogger<PostgresPersonaProfileReader> _logger;

	/// <summary>Creates the reader.</summary>
	public PostgresPersonaProfileReader(
		NpgsqlDataSource dataSource,
		ILogger<PostgresPersonaProfileReader> logger)
	{
		_dataSource = dataSource;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<PersonaRetrievalProfile> GetRetrievalProfileAsync(
		Guid personaId,
		CancellationToken cancellationToken = default)
	{
		if (personaId == Guid.Empty)
		{
			return PersonaRetrievalProfile.Neutral;
		}

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		// Skip deactivated personas -- they shouldn't tint retrieval
		// even if the caller's session still references the id.
		const string sql = """
			SELECT retrieval_profile::text, classification_floor
			FROM personas
			WHERE id = @id AND deactivated_at IS NULL
			""";
		string? rawJson = null;
		string? floorRaw = null;
		await using (var cmd = new NpgsqlCommand(sql, conn, tx))
		{
			cmd.Parameters.AddWithValue("id", personaId);
			await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				rawJson = reader.IsDBNull(0) ? null : reader.GetString(0);
				floorRaw = reader.IsDBNull(1) ? null : reader.GetString(1);
			}
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		if (rawJson is null)
		{
			return PersonaRetrievalProfile.Neutral;
		}

		PersonaRetrievalProfileDocument? doc;
		try
		{
			doc = JsonSerializer.Deserialize<PersonaRetrievalProfileDocument>(rawJson, _jsonOptions);
		}
		catch (JsonException ex)
		{
			_logger.LogWarning(ex,
				"PostgresPersonaProfileReader: persona {PersonaId} retrieval_profile JSON failed to parse; using neutral profile.",
				personaId);
			return PersonaRetrievalProfile.Neutral;
		}

		Classification? floor = null;
		if (!string.IsNullOrWhiteSpace(floorRaw)
			&& Enum.TryParse<Classification>(floorRaw, ignoreCase: false, out var parsedFloor))
		{
			floor = parsedFloor;
		}

		return doc?.ToProfile(floor) ?? PersonaRetrievalProfile.Neutral;
	}

	/// <inheritdoc />
	public async Task<PersonaSynthesisStyle> GetSynthesisStyleAsync(
		Guid personaId,
		CancellationToken cancellationToken = default)
	{
		if (personaId == Guid.Empty)
		{
			return PersonaSynthesisStyle.Neutral;
		}

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		const string sql = """
			SELECT synthesis_style::text
			FROM personas
			WHERE id = @id AND deactivated_at IS NULL
			""";
		string? rawJson = null;
		await using (var cmd = new NpgsqlCommand(sql, conn, tx))
		{
			cmd.Parameters.AddWithValue("id", personaId);
			var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			rawJson = result as string;
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		if (rawJson is null)
		{
			return PersonaSynthesisStyle.Neutral;
		}

		PersonaSynthesisStyleDocument? doc;
		try
		{
			doc = JsonSerializer.Deserialize<PersonaSynthesisStyleDocument>(rawJson, _jsonOptions);
		}
		catch (JsonException ex)
		{
			_logger.LogWarning(ex,
				"PostgresPersonaProfileReader: persona {PersonaId} synthesis_style JSON failed to parse; using neutral style.",
				personaId);
			return PersonaSynthesisStyle.Neutral;
		}

		return doc?.ToStyle() ?? PersonaSynthesisStyle.Neutral;
	}

	/// <summary>
	/// JSONB projection of the synthesis-style record per ADR 0015.
	/// Property names carry a <c>Raw</c> suffix to avoid clashing with
	/// the enum types of the same name when referenced from inside this
	/// nested class — the <c>JsonPropertyName</c> attribute maps to the
	/// ADR's documented field names.
	/// </summary>
	private sealed class PersonaSynthesisStyleDocument
	{
		[JsonPropertyName("answerLengthHint")]
		public string? AnswerLengthRaw { get; set; }

		[JsonPropertyName("structurePreference")]
		public string? StructureRaw { get; set; }

		[JsonPropertyName("citationDensity")]
		public string? CitationDensityRaw { get; set; }

		[JsonPropertyName("codeQuoting")]
		public string? CodeQuotingRaw { get; set; }

		[JsonPropertyName("hedgingPosture")]
		public string? HedgingRaw { get; set; }

		[JsonPropertyName("abstentionThreshold")]
		public double? AbstentionThreshold { get; set; }

		[JsonPropertyName("crossSourceSynthesis")]
		public string? CrossSourceRaw { get; set; }

		[JsonPropertyName("showSourceMetadata")]
		public bool? ShowSourceMetadata { get; set; }

		public PersonaSynthesisStyle ToStyle()
		{
			var neutral = PersonaSynthesisStyle.Neutral;
			return new PersonaSynthesisStyle(
				AnswerLength: ParseAnswerLength(AnswerLengthRaw) ?? neutral.AnswerLength,
				Structure: ParseStructure(StructureRaw) ?? neutral.Structure,
				CitationDensity: ParseCitationDensity(CitationDensityRaw) ?? neutral.CitationDensity,
				CodeQuoting: ParseCodeQuoting(CodeQuotingRaw) ?? neutral.CodeQuoting,
				HedgingPosture: ParseHedging(HedgingRaw) ?? neutral.HedgingPosture,
				AbstentionThreshold: AbstentionThreshold is double d && d >= 0 && d <= 1 ? d : neutral.AbstentionThreshold,
				CrossSourceSynthesis: ParseCrossSource(CrossSourceRaw) ?? neutral.CrossSourceSynthesis,
				ShowSourceMetadata: ShowSourceMetadata ?? neutral.ShowSourceMetadata);
		}

		// Permissive parsers: accept ADR-documented kebab-case forms
		// ("code-first") plus PascalCase enum names ("CodeFirst") plus
		// camelCase ("codeFirst"). Unknown values return null so the
		// caller substitutes the neutral default.
		private static AnswerLengthHint? ParseAnswerLength(string? raw) => Normalize(raw) switch
		{
			"short" or "brief" => AnswerLengthHint.Brief,
			"medium" => AnswerLengthHint.Medium,
			"long" or "extended" => AnswerLengthHint.Extended,
			_ => null,
		};

		private static StructurePreference? ParseStructure(string? raw) => Normalize(raw) switch
		{
			"narrative" => StructurePreference.Narrative,
			"bullet" => StructurePreference.Bullet,
			"tabular" => StructurePreference.Tabular,
			"codefirst" or "code-first" => StructurePreference.CodeFirst,
			_ => null,
		};

		private static CitationDensity? ParseCitationDensity(string? raw) => Normalize(raw) switch
		{
			"perclaim" or "per-claim" => CitationDensity.PerClaim,
			"perparagraph" or "per-paragraph" => CitationDensity.PerParagraph,
			"minimal" => CitationDensity.Minimal,
			_ => null,
		};

		private static CodeQuotingPreference? ParseCodeQuoting(string? raw) => Normalize(raw) switch
		{
			"preservecontext" or "preserve-context" => CodeQuotingPreference.PreserveContext,
			"minimal" => CodeQuotingPreference.Minimal,
			"inline" => CodeQuotingPreference.Inline,
			_ => null,
		};

		private static HedgingPosture? ParseHedging(string? raw) => Normalize(raw) switch
		{
			"calibrated" => HedgingPosture.Calibrated,
			"conservative" => HedgingPosture.Conservative,
			"direct" => HedgingPosture.Direct,
			_ => null,
		};

		private static CrossSourceSynthesisMode? ParseCrossSource(string? raw) => Normalize(raw) switch
		{
			"always" => CrossSourceSynthesisMode.Always,
			"whenneeded" or "when-needed" => CrossSourceSynthesisMode.WhenNeeded,
			"minimal" => CrossSourceSynthesisMode.Minimal,
			_ => null,
		};

		private static string? Normalize(string? raw)
			=> string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().ToLowerInvariant();
	}

	/// <summary>JSONB projection of the retrieval profile per ADR 0015.</summary>
	private sealed class PersonaRetrievalProfileDocument
	{
		[JsonPropertyName("sourceTypeWeights")]
		public Dictionary<string, double>? SourceTypeWeights { get; set; }

		[JsonPropertyName("recencyHalfLifeDays")]
		public int? RecencyHalfLifeDays { get; set; }

		[JsonPropertyName("authorityBias")]
		public Dictionary<string, double>? AuthorityBias { get; set; }

		[JsonPropertyName("crossDepartmentBoost")]
		public CrossDepartmentBoostDocument? CrossDepartmentBoost { get; set; }

		[JsonPropertyName("floorClassification")]
		public string? FloorClassification { get; set; }

		public PersonaRetrievalProfile ToProfile(Classification? columnFloor)
		{
			var sourceTypes = SourceTypeWeights is null
				? new Dictionary<string, double>(StringComparer.Ordinal)
				: new Dictionary<string, double>(SourceTypeWeights, StringComparer.Ordinal);
			var authority = AuthorityBias is null
				? new Dictionary<string, double>(StringComparer.Ordinal)
				: new Dictionary<string, double>(AuthorityBias, StringComparer.Ordinal);

			PersonaCrossDepartmentBoost? cross = null;
			if (CrossDepartmentBoost is not null)
			{
				cross = new PersonaCrossDepartmentBoost(
					SameDepartment: CrossDepartmentBoost.SameDepartment ?? 1.0,
					CrossDepartmentInternal: CrossDepartmentBoost.CrossDepartmentInternal ?? 1.0,
					CrossDepartmentShared: CrossDepartmentBoost.CrossDepartmentShared ?? 1.0);
			}

			Classification? floor = columnFloor;
			if (!string.IsNullOrWhiteSpace(FloorClassification)
				&& Enum.TryParse<Classification>(FloorClassification, ignoreCase: false, out var jsonFloor))
			{
				// JSON echo wins ties with the column only when the column
				// hadn't been parsed -- the column is authoritative per ADR 0015.
				floor ??= jsonFloor;
			}

			return new PersonaRetrievalProfile(
				SourceTypeWeights: sourceTypes,
				RecencyHalfLifeDays: RecencyHalfLifeDays,
				AuthorityBias: authority,
				CrossDepartmentBoost: cross,
				FloorClassification: floor);
		}
	}

	private sealed class CrossDepartmentBoostDocument
	{
		[JsonPropertyName("sameDepartment")]
		public double? SameDepartment { get; set; }

		[JsonPropertyName("crossDepartmentInternal")]
		public double? CrossDepartmentInternal { get; set; }

		[JsonPropertyName("crossDepartmentShared")]
		public double? CrossDepartmentShared { get; set; }
	}
}

/// <summary>Null fallback used when Postgres isn't configured.</summary>
internal sealed class NullPersonaProfileReader : IPersonaProfileReader
{
	public Task<PersonaRetrievalProfile> GetRetrievalProfileAsync(
		Guid personaId,
		CancellationToken cancellationToken = default)
		=> Task.FromResult(PersonaRetrievalProfile.Neutral);

	public Task<PersonaSynthesisStyle> GetSynthesisStyleAsync(
		Guid personaId,
		CancellationToken cancellationToken = default)
		=> Task.FromResult(PersonaSynthesisStyle.Neutral);
}
