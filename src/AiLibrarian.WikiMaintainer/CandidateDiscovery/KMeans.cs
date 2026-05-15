namespace AiLibrarian.WikiMaintainer.CandidateDiscovery;

/// <summary>
/// Tiny k-means clustering helper used by the candidate-page discovery
/// flow. Pure stdlib — no MathNet, no ML.NET, no extra dependencies.
/// The chunk-discovery use case is small (≤500 vectors of 1536 dims),
/// converges within ~20 iterations, and is called at most once per
/// operator request, so the simple implementation is plenty.
///
/// <para>Uses k-means++ seeding (probabilistic, distance-weighted)
/// rather than uniform-random. Random clusters often collapse on text
/// embeddings because dense regions of the unit hypersphere swallow
/// multiple seeds; k-means++ spreads them out.</para>
///
/// <para>Cosine distance, not Euclidean. Text embeddings are typically
/// L2-normalized so the two are monotonically related, but using cosine
/// directly is cheaper for normalized inputs and avoids drift if the
/// embedding provider stops normalizing.</para>
/// </summary>
internal static class KMeans
{
	/// <summary>
	/// Cluster <paramref name="vectors"/> into <paramref name="k"/> groups.
	/// Returns one cluster-index per input vector, in the same order.
	/// </summary>
	/// <param name="vectors">Input vectors. All must have the same length.</param>
	/// <param name="k">Number of clusters. Clamped to <c>[1, vectors.Length]</c>.</param>
	/// <param name="maxIterations">Iteration cap; convergence usually inside 10-20.</param>
	/// <param name="random">Seed-able RNG; deterministic tests pass a fixed Random.</param>
	public static int[] Cluster(
		IReadOnlyList<float[]> vectors,
		int k,
		int maxIterations = 30,
		Random? random = null)
	{
		ArgumentNullException.ThrowIfNull(vectors);
		if (vectors.Count == 0)
		{
			return Array.Empty<int>();
		}
		var rng = random ?? Random.Shared;
		var n = vectors.Count;
		var dim = vectors[0].Length;
		var actualK = Math.Clamp(k, 1, n);

		// k-means++ seeding.
		var centroids = SeedKMeansPlusPlus(vectors, actualK, rng);
		var assignments = new int[n];
		var counts = new int[actualK];

		for (var iter = 0; iter < maxIterations; iter++)
		{
			var changed = false;
			// Assign every vector to the nearest centroid (1 - cosine
			// similarity = cosine distance for normalized inputs; for
			// un-normalized we compute it explicitly).
			for (var i = 0; i < n; i++)
			{
				var nearest = 0;
				var nearestDist = CosineDistance(vectors[i], centroids[0]);
				for (var c = 1; c < actualK; c++)
				{
					var d = CosineDistance(vectors[i], centroids[c]);
					if (d < nearestDist)
					{
						nearestDist = d;
						nearest = c;
					}
				}
				if (assignments[i] != nearest)
				{
					assignments[i] = nearest;
					changed = true;
				}
			}

			if (!changed)
			{
				break;
			}

			// Recompute centroids as the L2-normalized mean of their members.
			for (var c = 0; c < actualK; c++)
			{
				Array.Clear(centroids[c]);
				counts[c] = 0;
			}
			for (var i = 0; i < n; i++)
			{
				var c = assignments[i];
				var v = vectors[i];
				var cent = centroids[c];
				for (var d = 0; d < dim; d++)
				{
					cent[d] += v[d];
				}
				counts[c]++;
			}
			for (var c = 0; c < actualK; c++)
			{
				if (counts[c] == 0)
				{
					// Reseed an empty cluster from a random data point so
					// the next iteration has something to gravitate to.
					var src = vectors[rng.Next(n)];
					Array.Copy(src, centroids[c], dim);
					continue;
				}
				Normalize(centroids[c]);
			}
		}

		return assignments;
	}

	private static float[][] SeedKMeansPlusPlus(IReadOnlyList<float[]> vectors, int k, Random rng)
	{
		var n = vectors.Count;
		var dim = vectors[0].Length;
		var centroids = new float[k][];

		// First centroid: uniform random pick.
		centroids[0] = (float[])vectors[rng.Next(n)].Clone();

		// Distance-squared from nearest already-chosen centroid; updated
		// incrementally as we add centroids.
		var distSq = new double[n];
		for (var i = 0; i < n; i++)
		{
			var d = CosineDistance(vectors[i], centroids[0]);
			distSq[i] = d * d;
		}

		for (var c = 1; c < k; c++)
		{
			var total = distSq.Sum();
			if (total <= 0)
			{
				// All points coincide with chosen centroids -- pick uniformly.
				centroids[c] = (float[])vectors[rng.Next(n)].Clone();
			}
			else
			{
				var pick = rng.NextDouble() * total;
				var running = 0.0;
				var chosen = n - 1;
				for (var i = 0; i < n; i++)
				{
					running += distSq[i];
					if (running >= pick)
					{
						chosen = i;
						break;
					}
				}
				centroids[c] = (float[])vectors[chosen].Clone();
			}
			// Update distSq with the new centroid.
			for (var i = 0; i < n; i++)
			{
				var d = CosineDistance(vectors[i], centroids[c]);
				var dsq = d * d;
				if (dsq < distSq[i])
				{
					distSq[i] = dsq;
				}
			}
		}

		return centroids;
	}

	/// <summary>Cosine distance = 1 - cosine similarity. Range [0, 2]; 0 = identical direction.</summary>
	internal static double CosineDistance(float[] a, float[] b)
	{
		double dot = 0;
		double na = 0;
		double nb = 0;
		for (var i = 0; i < a.Length; i++)
		{
			dot += a[i] * b[i];
			na += a[i] * a[i];
			nb += b[i] * b[i];
		}
		if (na == 0 || nb == 0)
		{
			return 1.0;
		}
		return 1.0 - (dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
	}

	private static void Normalize(float[] v)
	{
		double norm = 0;
		for (var i = 0; i < v.Length; i++)
		{
			norm += v[i] * v[i];
		}
		if (norm == 0)
		{
			return;
		}
		var inv = 1.0 / Math.Sqrt(norm);
		for (var i = 0; i < v.Length; i++)
		{
			v[i] = (float)(v[i] * inv);
		}
	}
}
