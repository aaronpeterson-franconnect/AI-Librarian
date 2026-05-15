using AiLibrarian.Domain;
using AiLibrarian.Infrastructure.Rls;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// Read-side repository for <see cref="Department"/>. RLS on
/// <c>departments</c> is "any authenticated principal" per
/// changeset <c>0099-rls-departments-1</c>; this interface adds no
/// extra filtering and lets RLS do the work.
/// </summary>
public interface IDepartmentRepository
{
	/// <summary>
	/// List active departments visible to the caller. Deactivated
	/// departments (<c>deactivated_at IS NOT NULL</c>) are excluded —
	/// callers who need them can layer a different query on top.
	/// </summary>
	Task<IReadOnlyList<Department>> ListActiveAsync(
		RlsSessionContext context,
		CancellationToken cancellationToken = default);
}
