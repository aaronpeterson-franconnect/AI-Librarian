using System.Diagnostics;

namespace AiLibrarian.Cli.Mcp;

/// <summary>Starts the MCP host assembly with inherited stdio so Cursor / Claude can attach.</summary>
internal static class McpHostRunner
{
	internal static int Run(string mcpDllPath, IReadOnlyDictionary<string, string>? environment = null)
	{
		if (string.IsNullOrWhiteSpace(mcpDllPath) || !File.Exists(mcpDllPath))
		{
			Console.Error.WriteLine($"MCP host not found: {mcpDllPath}");
			return 3;
		}

		var psi = new ProcessStartInfo
		{
			FileName = "dotnet",
			Arguments = $"exec \"{mcpDllPath}\"",
			UseShellExecute = false,
		};

		if (environment is not null)
		{
			foreach (var (key, value) in environment)
			{
				psi.Environment[key] = value;
			}
		}

		using var process = Process.Start(psi);
		if (process is null)
		{
			Console.Error.WriteLine("Failed to start dotnet process.");
			return 4;
		}

		process.WaitForExit();
		return process.ExitCode;
	}

	internal static string? ResolveMcpDllPath(string? explicitPath)
	{
		if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
		{
			return explicitPath;
		}

		var env = Environment.GetEnvironmentVariable("AILIB_MCP_DLL");
		if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
		{
			return env;
		}

		var candidate = Path.Combine(AppContext.BaseDirectory, "AiLibrarian.Mcp.dll");
		if (File.Exists(candidate))
		{
			return candidate;
		}

		return null;
	}
}
