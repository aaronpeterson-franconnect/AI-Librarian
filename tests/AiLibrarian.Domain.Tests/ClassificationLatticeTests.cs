using AiLibrarian.Domain;

namespace AiLibrarian.Domain.Tests;

public sealed class ClassificationLatticeTests
{
	[Theory]
	[InlineData(Classification.Public, Classification.Public)]
	[InlineData(Classification.Internal, Classification.Public)]
	[InlineData(Classification.Internal, Classification.Internal)]
	[InlineData(Classification.Confidential, Classification.Internal)]
	[InlineData(Classification.Restricted, Classification.Confidential)]
	[InlineData(Classification.Restricted, Classification.Restricted)]
	public void MayCite_returns_true_when_cited_is_at_or_below_facet(
		Classification facet, Classification cited)
	{
		ClassificationLattice.MayCite(facet, cited).Should().BeTrue();
	}

	[Theory]
	[InlineData(Classification.Public, Classification.Internal)]
	[InlineData(Classification.Public, Classification.Confidential)]
	[InlineData(Classification.Internal, Classification.Confidential)]
	[InlineData(Classification.Internal, Classification.Restricted)]
	[InlineData(Classification.Confidential, Classification.Restricted)]
	public void MayCite_returns_false_when_cited_is_above_facet(
		Classification facet, Classification cited)
	{
		ClassificationLattice.MayCite(facet, cited).Should().BeFalse();
	}

	[Fact]
	public void Lattice_order_is_public_internal_confidential_restricted()
	{
		((int)Classification.Public).Should().Be(0);
		((int)Classification.Internal).Should().Be(1);
		((int)Classification.Confidential).Should().Be(2);
		((int)Classification.Restricted).Should().Be(3);
	}
}
