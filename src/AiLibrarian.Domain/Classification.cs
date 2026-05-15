namespace AiLibrarian.Domain;

/// <summary>
/// Four-tier data classification — the default access boundary
/// per ADR 0011. The lattice from least-to-most-sensitive is
/// <see cref="Public"/> &lt; <see cref="Internal"/> &lt;
/// <see cref="Confidential"/> &lt; <see cref="Restricted"/>.
/// </summary>
public enum Classification
{
	/// <summary>World-readable; no auth required for content access.</summary>
	Public = 0,

	/// <summary>Default. Readable by any authenticated employee
	/// (<c>app.is_employee = true</c>); cross-department reading
	/// of <see cref="Internal"/> sources is the normal case.</summary>
	Internal = 1,

	/// <summary>Readable by members of the owning department, plus
	/// any departments holding an active <c>source_shares</c> grant.</summary>
	Confidential = 2,

	/// <summary>Readable only by Librarian+ members of the owning
	/// department, plus any departments holding an active
	/// <c>source_shares</c> grant (Admin-issued for Restricted).</summary>
	Restricted = 3,
}

/// <summary>
/// Helpers for the classification lattice.
/// </summary>
public static class ClassificationLattice
{
	/// <summary>
	/// Returns true when <paramref name="cited"/> may be cited from a
	/// claim in a facet at <paramref name="facet"/>'s minimum tier.
	/// Implements the validator rule 6 from ADR 0007.
	/// </summary>
	public static bool MayCite(Classification facet, Classification cited)
	{
		return cited <= facet;
	}
}
