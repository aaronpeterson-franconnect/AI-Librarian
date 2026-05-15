using System.Text.Json;

namespace AiLibrarian.Infrastructure.Tests.Auditing;

/// <summary>
/// Pin the serialization contract used by <c>PostgresAuditWriter</c>'s
/// <c>details</c> column round-trip. The writer uses
/// <see cref="JsonSerializer"/> defaults (no custom converters) so
/// these tests pin exactly what survives round-trip and what becomes
/// a <see cref="JsonElement"/>. If we ever need richer CLR-type
/// preservation for the Phase 2 dashboard, we'll need a converter
/// pass — for now, the JSONB is the source of truth and CLR
/// reconstruction is best-effort.
/// </summary>
public sealed class AuditDetailsSerializationTests
{
	[Fact]
	public void Simple_string_round_trips_as_string()
	{
		var details = new Dictionary<string, object?> { ["finish_reason"] = "stop" };

		var hydrated = RoundTrip(details);

		((JsonElement)hydrated["finish_reason"]!).GetString().Should().Be("stop");
	}

	[Fact]
	public void Integers_round_trip_via_JsonElement()
	{
		var details = new Dictionary<string, object?> { ["generated_chars"] = 4096 };

		var hydrated = RoundTrip(details);

		((JsonElement)hydrated["generated_chars"]!).GetInt32().Should().Be(4096);
	}

	[Fact]
	public void Booleans_round_trip_via_JsonElement()
	{
		var details = new Dictionary<string, object?> { ["degraded_mode_allowed"] = true };

		var hydrated = RoundTrip(details);

		((JsonElement)hydrated["degraded_mode_allowed"]!).GetBoolean().Should().BeTrue();
	}

	[Fact]
	public void Null_values_round_trip_as_clr_null()
	{
		// System.Text.Json deserializes JSON `null` to a CLR `null` rather
		// than a JsonElement of kind Null. Documented quirk worth pinning
		// — readers must check for null before casting to JsonElement.
		var details = new Dictionary<string, object?> { ["error_class"] = null };

		var hydrated = RoundTrip(details);

		hydrated.Should().ContainKey("error_class");
		hydrated["error_class"].Should().BeNull();
	}

	[Fact]
	public void Guid_round_trips_as_string_element()
	{
		// Guids serialize via System.Text.Json's default ToString() — this
		// is a documented loss: callers must reparse on read. Pinned so a
		// silent change of behavior breaks the test.
		var id = new Guid("e8c5fe26-cf78-4d61-9c41-72bf7c8d4a02");
		var details = new Dictionary<string, object?> { ["source_id"] = id };

		var hydrated = RoundTrip(details);

		var elem = (JsonElement)hydrated["source_id"]!;
		elem.ValueKind.Should().Be(JsonValueKind.String);
		Guid.Parse(elem.GetString()!).Should().Be(id);
	}

	[Fact]
	public void DateTimeOffset_round_trips_as_iso8601_string()
	{
		var when = new DateTimeOffset(2026, 5, 5, 12, 34, 56, TimeSpan.Zero);
		var details = new Dictionary<string, object?> { ["occurred_at"] = when };

		var hydrated = RoundTrip(details);

		var elem = (JsonElement)hydrated["occurred_at"]!;
		elem.ValueKind.Should().Be(JsonValueKind.String);
		DateTimeOffset.Parse(elem.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
			.Should().Be(when);
	}

	[Fact]
	public void Nested_object_round_trips_as_JsonElement_subtree()
	{
		var details = new Dictionary<string, object?>
		{
			["llm_metadata"] = new Dictionary<string, object?>
			{
				["finish_reason"] = "stop",
				["tokens"] = 42,
			},
		};

		var hydrated = RoundTrip(details);

		var nested = (JsonElement)hydrated["llm_metadata"]!;
		nested.ValueKind.Should().Be(JsonValueKind.Object);
		nested.GetProperty("finish_reason").GetString().Should().Be("stop");
		nested.GetProperty("tokens").GetInt32().Should().Be(42);
	}

	[Fact]
	public void Array_round_trips_as_JsonElement_array()
	{
		var details = new Dictionary<string, object?>
		{
			["chunk_ids"] = new[] { "a", "b", "c" },
		};

		var hydrated = RoundTrip(details);

		var arr = (JsonElement)hydrated["chunk_ids"]!;
		arr.ValueKind.Should().Be(JsonValueKind.Array);
		arr.GetArrayLength().Should().Be(3);
	}

	private static Dictionary<string, object?> RoundTrip(IReadOnlyDictionary<string, object?> details)
	{
		// Mirrors PostgresAuditWriter.WriteAsync (Serialize) +
		// PostgresAuditWriter.MapEvent (Deserialize<Dictionary<string, object?>>).
		var json = JsonSerializer.Serialize(details);
		return JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;
	}
}
