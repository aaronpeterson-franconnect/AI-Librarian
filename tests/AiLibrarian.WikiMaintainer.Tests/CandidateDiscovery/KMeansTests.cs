using AiLibrarian.WikiMaintainer.CandidateDiscovery;

namespace AiLibrarian.WikiMaintainer.Tests.CandidateDiscovery;

/// <summary>
/// Unit coverage for the candidate-discovery k-means helper. The
/// algorithm is hidden from public consumers, but the
/// <see cref="WikiPageCandidateGenerator"/> depends on it producing
/// stable, reasonably-separated clusters for embedding-style inputs.
/// </summary>
public sealed class KMeansTests
{
	[Fact]
	public void Cluster_returns_one_label_per_input()
	{
		var rng = new Random(42);
		var vectors = Enumerable.Range(0, 20)
			.Select(_ => RandomUnitVector(8, rng))
			.ToList();

		var labels = KMeans.Cluster(vectors, k: 3, random: new Random(0));

		labels.Length.Should().Be(vectors.Count);
		labels.Should().AllSatisfy(l => l.Should().BeInRange(0, 2));
	}

	[Fact]
	public void Cluster_separates_two_well_separated_clouds()
	{
		// Two clouds: one centered on (1,0,0,...), one on (-1,0,0,...).
		// K-means with k=2 must split them.
		var rng = new Random(123);
		var cloudA = Enumerable.Range(0, 15)
			.Select(_ => OffsetUnitVector(dim: 8, offsetAxis: 0, offsetValue: 1.0f, rng: rng))
			.ToList();
		var cloudB = Enumerable.Range(0, 15)
			.Select(_ => OffsetUnitVector(dim: 8, offsetAxis: 0, offsetValue: -1.0f, rng: rng))
			.ToList();
		var all = cloudA.Concat(cloudB).ToList();

		var labels = KMeans.Cluster(all, k: 2, random: new Random(0));

		// Every cloudA member should share a label; every cloudB member
		// should share the other label.
		var aLabels = labels.Take(15).Distinct().ToList();
		var bLabels = labels.Skip(15).Take(15).Distinct().ToList();

		aLabels.Should().HaveCount(1, "cloud A members all cluster together");
		bLabels.Should().HaveCount(1, "cloud B members all cluster together");
		aLabels[0].Should().NotBe(bLabels[0], "the two clouds got different labels");
	}

	[Fact]
	public void Cluster_handles_k_larger_than_input_by_clamping()
	{
		var vectors = new[]
		{
			new float[] { 1, 0, 0 },
			new float[] { 0, 1, 0 },
		};

		var labels = KMeans.Cluster(vectors, k: 10);

		labels.Length.Should().Be(2);
		labels.Distinct().Count().Should().BeLessThanOrEqualTo(2, "can't have more clusters than points");
	}

	[Fact]
	public void Cluster_returns_empty_for_empty_input()
	{
		var labels = KMeans.Cluster(Array.Empty<float[]>(), k: 5);
		labels.Should().BeEmpty();
	}

	[Fact]
	public void Cluster_with_k_equals_one_returns_all_zeros()
	{
		var rng = new Random(7);
		var vectors = Enumerable.Range(0, 10).Select(_ => RandomUnitVector(4, rng)).ToList();

		var labels = KMeans.Cluster(vectors, k: 1);

		labels.Should().AllSatisfy(l => l.Should().Be(0));
	}

	[Fact]
	public void CosineDistance_is_zero_for_identical_vectors()
	{
		var v = new float[] { 1.0f, 2.0f, -0.5f };
		var d = KMeans.CosineDistance(v, v);
		d.Should().BeApproximately(0.0, 1e-9);
	}

	[Fact]
	public void CosineDistance_is_two_for_anti_parallel_vectors()
	{
		var a = new float[] { 1.0f, 0.0f };
		var b = new float[] { -1.0f, 0.0f };
		var d = KMeans.CosineDistance(a, b);
		d.Should().BeApproximately(2.0, 1e-9);
	}

	private static float[] RandomUnitVector(int dim, Random rng)
	{
		var v = new float[dim];
		for (var i = 0; i < dim; i++)
		{
			v[i] = (float)(rng.NextDouble() - 0.5);
		}
		Normalize(v);
		return v;
	}

	private static float[] OffsetUnitVector(int dim, int offsetAxis, float offsetValue, Random rng)
	{
		var v = new float[dim];
		for (var i = 0; i < dim; i++)
		{
			v[i] = (float)((rng.NextDouble() - 0.5) * 0.05);
		}
		v[offsetAxis] += offsetValue;
		Normalize(v);
		return v;
	}

	private static void Normalize(float[] v)
	{
		double n = 0;
		for (var i = 0; i < v.Length; i++)
		{
			n += v[i] * v[i];
		}
		if (n == 0)
		{
			return;
		}
		var inv = 1.0 / Math.Sqrt(n);
		for (var i = 0; i < v.Length; i++)
		{
			v[i] = (float)(v[i] * inv);
		}
	}
}
