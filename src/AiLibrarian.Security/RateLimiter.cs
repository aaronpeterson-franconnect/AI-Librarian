using System.Collections.Concurrent;

namespace AiLibrarian.Security;

/// <summary>
/// Token-bucket rate limiter keyed by caller identity (Entra OID).
/// Defends against ADR 0017 T4 / T2 by capping ask-call burst from
/// any one caller. In-process for now; Phase 2 distributed instance
/// (Redis or similar) when MCP scales horizontally.
/// </summary>
public sealed class RateLimiter
{
	private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
	private readonly Func<DateTimeOffset> _clock;
	private readonly int _capacity;
	private readonly double _refillTokensPerSecond;

	/// <summary>Creates the limiter from options.</summary>
	public RateLimiter(AskGuardOptions options, Func<DateTimeOffset>? clock = null)
	{
		ArgumentNullException.ThrowIfNull(options);
		_capacity = Math.Max(1, options.RateLimitPerMinutePerCaller);
		_refillTokensPerSecond = _capacity / 60.0;
		_clock = clock ?? (() => DateTimeOffset.UtcNow);
	}

	/// <summary>
	/// Try to consume one token for <paramref name="callerId"/>. Returns
	/// true when admitted, false when the caller is over the limit.
	/// </summary>
	public bool TryAcquire(string callerId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(callerId);

		var now = _clock();
		var bucket = _buckets.GetOrAdd(callerId, _ => new Bucket(_capacity, now));

		lock (bucket)
		{
			var elapsed = (now - bucket.LastRefill).TotalSeconds;
			if (elapsed > 0)
			{
				bucket.Tokens = Math.Min(_capacity, bucket.Tokens + (elapsed * _refillTokensPerSecond));
				bucket.LastRefill = now;
			}

			if (bucket.Tokens >= 1.0)
			{
				bucket.Tokens -= 1.0;
				return true;
			}

			return false;
		}
	}

	private sealed class Bucket
	{
		public double Tokens;
		public DateTimeOffset LastRefill;

		public Bucket(int capacity, DateTimeOffset now)
		{
			Tokens = capacity;
			LastRefill = now;
		}
	}
}
