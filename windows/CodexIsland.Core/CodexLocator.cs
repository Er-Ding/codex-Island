using System.Runtime.InteropServices;

namespace CodexIsland.Core;

public static class CodexLocator
{
    public static string? Find(string? explicitPath = null)
    {
        var configured = explicitPath ?? Environment.GetEnvironmentVariable("CODEX_ISLAND_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(configured)) return Executable(configured);
        return Candidates().Select(Executable).FirstOrDefault(p => p is not null);
    }

    private static string? Executable(string path)
    {
        try
        {
            path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim('"')));
            return Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static IEnumerable<string> Candidates()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // The desktop app extracts a native CLI here. Never hard-code a package version/hash.
        var cache = Path.Combine(local, "OpenAI", "Codex", "bin");
        string[] versions;
        try { versions = Directory.Exists(cache) ? Directory.GetDirectories(cache).OrderByDescending(Directory.GetLastWriteTimeUtc).ToArray() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { versions = []; }
        foreach (var version in versions) yield return Path.Combine(version, "codex.exe");
        yield return Path.Combine(home, ".local", "bin", "codex.exe");

        var pathFolders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim('"'));
        var npmFolders = pathFolders.Prepend(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var architectures = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? new[] { "arm64", "x64" } : new[] { "x64" };
        foreach (var folder in npmFolders)
        {
            yield return Path.Combine(folder, "codex.exe");
            var package = Path.Combine(folder, "node_modules", "@openai", "codex");
            foreach (var arch in architectures)
            {
                var triple = arch == "arm64" ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
                var roots = new[]
                {
                    Path.Combine(package, "node_modules", "@openai", $"codex-win32-{arch}", "vendor"),
                    Path.Combine(folder, "node_modules", "@openai", $"codex-win32-{arch}", "vendor"),
                    Path.Combine(package, "vendor")
                };
                foreach (var root in roots)
                    foreach (var bin in new[] { "bin", "codex" }) yield return Path.Combine(root, triple, bin, "codex.exe");
            }
        }
    }
}
