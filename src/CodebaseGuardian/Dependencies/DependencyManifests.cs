namespace CodebaseGuardian.Dependencies;

/// <summary>Recognises the files that declare or lock a project's dependencies.</summary>
public static class DependencyManifests
{
    private static readonly Dictionary<string, string> ByFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Directory.Packages.props"] = "nuget",
        ["Directory.Build.props"] = "nuget",
        ["packages.lock.json"] = "nuget",
        ["global.json"] = "nuget",
        ["package.json"] = "npm",
        ["package-lock.json"] = "npm",
        ["npm-shrinkwrap.json"] = "npm",
        ["yarn.lock"] = "npm",
        ["pnpm-lock.yaml"] = "npm",
    };

    private static readonly string[] NuGetProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    public static bool IsManifest(string relativePath) => GetEcosystem(relativePath) is not null;

    /// <summary>"nuget", "npm", or null when the path is not a dependency manifest.</summary>
    public static string? GetEcosystem(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return null;
        }

        var name = relativePath[(relativePath.LastIndexOfAny(['/', '\\']) + 1)..];
        if (ByFileName.TryGetValue(name, out var ecosystem))
        {
            return ecosystem;
        }

        return NuGetProjectExtensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) ? "nuget" : null;
    }
}
