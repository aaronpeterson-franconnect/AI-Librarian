namespace AiLibrarian.Domain;

/// <summary>
/// Role within a department, plus system-wide <see cref="Admin"/>.
/// Per ADR 0005. The first four form a strict ladder where each role
/// includes the prior role's capabilities; <see cref="Admin"/> is
/// orthogonal and system-wide.
/// </summary>
public enum Role
{
	/// <summary>Query-only; can read sources visible per the read predicate.</summary>
	Reader = 0,

	/// <summary>Reader + can submit sources to the approval queue.</summary>
	Contributor = 1,

	/// <summary>Contributor + can approve/reject queue items and soft-delete.</summary>
	Reviewer = 2,

	/// <summary>Reviewer + can edit policy/directives, lock pages,
	/// hard-delete, and initiate concept-level RTBF.</summary>
	Librarian = 3,

	/// <summary>System-wide. Creates departments, assigns librarians,
	/// views all audit, runs system-wide RTBF. Always granted with
	/// <c>department_id = NULL</c>.</summary>
	Admin = 4,
}
