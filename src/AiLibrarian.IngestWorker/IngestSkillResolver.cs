using AiLibrarian.Domain.Ingest;
using AiLibrarian.Domain.Skills;

namespace AiLibrarian.IngestWorker;

/// <summary>Selects a skill from ingest hints (MIME first, then file extension).</summary>
internal static class IngestSkillResolver
{
	public static ISkill? Resolve(ISkillRegistry registry, IngestJobMessage job)
	{
		if (!string.IsNullOrWhiteSpace(job.ContentType))
		{
			var byMime = registry.ResolveByMimeType(job.ContentType.Trim());
			if (byMime is not null)
			{
				return byMime;
			}
		}

		if (!string.IsNullOrWhiteSpace(job.OriginalFileName))
		{
			var ext = Path.GetExtension(job.OriginalFileName.AsSpan().Trim());
			if (ext.Length > 0)
			{
				return registry.ResolveByExtension(ext.ToString());
			}
		}

		return null;
	}
}
