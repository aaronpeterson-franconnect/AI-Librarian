using Microsoft.AspNetCore.Http;

namespace AiLibrarian.Api.Telemetry;

/// <summary>
/// Resolves a correlation ID for every inbound request and mirrors it
/// onto the response so callers (and tests) can correlate downstream
/// audit rows back to a single client interaction.
///
/// <para>
/// Resolution order (first match wins):
/// </para>
/// <list type="number">
///   <item><description>
///     <c>traceparent</c> header (W3C trace context) — extract the
///     16-byte trace-id and rehash to a Guid for use as the
///     <c>correlation_id</c> column. Keeps the audit ledger linkable
///     to App Insights / OTel traces.
///   </description></item>
///   <item><description>
///     <c>X-Correlation-Id</c> header — accepted as a Guid string;
///     malformed values are ignored.
///   </description></item>
///   <item><description>
///     A freshly minted Guid — recorded so the response header still
///     carries something meaningful for the client.
///   </description></item>
/// </list>
/// </summary>
public sealed class CorrelationIdMiddleware
{
	/// <summary>HTTP header carrying the correlation ID on inbound requests and outbound responses.</summary>
	public const string HeaderName = "X-Correlation-Id";

	private const string ItemsKey = "AiLibrarian.CorrelationId";
	private const string TraceParentHeader = "traceparent";

	private readonly RequestDelegate _next;

	/// <summary>Creates the middleware.</summary>
	public CorrelationIdMiddleware(RequestDelegate next)
	{
		_next = next;
	}

	/// <summary>Per-request entry point.</summary>
	public async Task InvokeAsync(HttpContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		var id = ResolveFromTraceParent(context) ?? ResolveFromCorrelationHeader(context) ?? Guid.NewGuid();
		context.Items[ItemsKey] = id;

		// Mirror onto the response so the client (and integration tests)
		// can pin the value used for downstream audit / tracing.
		context.Response.OnStarting(state =>
		{
			var ctx = (HttpContext)state;
			if (!ctx.Response.Headers.ContainsKey(HeaderName))
			{
				ctx.Response.Headers[HeaderName] = id.ToString("D");
			}

			return Task.CompletedTask;
		}, context);

		await _next(context).ConfigureAwait(false);
	}

	/// <summary>
	/// Pull the correlation ID stashed on the current
	/// <see cref="HttpContext"/>, or <see langword="null"/> if the
	/// middleware hasn't run (non-HTTP code paths).
	/// </summary>
	internal static Guid? TryGet(HttpContext context)
	{
		if (context.Items.TryGetValue(ItemsKey, out var value) && value is Guid g)
		{
			return g;
		}

		return null;
	}

	private static Guid? ResolveFromTraceParent(HttpContext context)
	{
		// W3C trace-context: "00-<32 hex trace-id>-<16 hex parent-id>-<2 hex flags>".
		if (!context.Request.Headers.TryGetValue(TraceParentHeader, out var tp) || tp.Count == 0)
		{
			return null;
		}

		var parts = tp[0]?.Split('-');
		if (parts is not { Length: 4 } || parts[1].Length != 32)
		{
			return null;
		}

		// Take the first 16 bytes of the trace-id as a Guid; the trace-id
		// is opaque hex, so any well-formed 16-byte slice is valid.
		Span<byte> bytes = stackalloc byte[16];
		if (!TryParseHex(parts[1].AsSpan(0, 32), bytes))
		{
			return null;
		}

		return new Guid(bytes);
	}

	private static Guid? ResolveFromCorrelationHeader(HttpContext context)
	{
		if (!context.Request.Headers.TryGetValue(HeaderName, out var hdr) || hdr.Count == 0)
		{
			return null;
		}

		return Guid.TryParse(hdr[0], out var parsed) ? parsed : null;
	}

	private static bool TryParseHex(ReadOnlySpan<char> hex, Span<byte> bytes)
	{
		if (hex.Length != bytes.Length * 2)
		{
			return false;
		}

		for (var i = 0; i < bytes.Length; i++)
		{
			if (!byte.TryParse(hex.Slice(i * 2, 2), System.Globalization.NumberStyles.HexNumber,
				System.Globalization.CultureInfo.InvariantCulture, out var b))
			{
				return false;
			}

			bytes[i] = b;
		}

		return true;
	}
}

/// <summary>
/// HttpContext-backed <see cref="ICorrelationIdAccessor"/>. Falls back
/// to a transient Guid when invoked outside an HTTP request (background
/// services, tests) so callers never see <see cref="Guid.Empty"/>.
/// </summary>
internal sealed class HttpContextCorrelationIdAccessor : ICorrelationIdAccessor
{
	private readonly IHttpContextAccessor _http;

	public HttpContextCorrelationIdAccessor(IHttpContextAccessor http)
	{
		_http = http;
	}

	public Guid Current
	{
		get
		{
			var ctx = _http.HttpContext;
			if (ctx is not null)
			{
				var resolved = CorrelationIdMiddleware.TryGet(ctx);
				if (resolved.HasValue)
				{
					return resolved.Value;
				}
			}

			return Guid.NewGuid();
		}
	}
}
