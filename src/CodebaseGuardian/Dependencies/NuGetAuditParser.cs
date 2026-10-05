using System.Text.Json;

namespace CodebaseGuardian.Dependencies;

/// <summary>Parses <c>dotnet list package --format json --output-version 1</c>.</summary>
public static class NuGetAuditParser
{
    private const string Ecosystem = "nuget";

    /// <exception cref="JsonException">The text is not the expected JSON.</exception>
    public static IReadOnlyList<VulnerablePackage> ParseVulnerable(string json)
    {
        var found = new Dictionary<(string, string, string?), VulnerablePackage>();
        foreach (var (project, package) in Packages(json, includeTransitive: true))
        {
            var id = Text(package, "id");
            var version = Text(package, "resolvedVersion");
            if (id is null || version is null || !package.TryGetProperty("vulnerabilities", out var vulnerabilities)
                || vulnerabilities.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var vulnerability in vulnerabilities.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Object))
            {
                found.TryAdd((id, version, project), new VulnerablePackage(
                    Ecosystem, id, version, Text(vulnerability, "severity") ?? "Unknown", Text(vulnerability, "advisoryurl"), project));
            }
        }

        return [.. found.Values];
    }

    /// <exception cref="JsonException">The text is not the expected JSON.</exception>
    public static IReadOnlyList<OutdatedPackage> ParseOutdated(string json)
    {
        var found = new Dictionary<(string, string, string?), OutdatedPackage>();
        foreach (var (project, package) in Packages(json, includeTransitive: false))
        {
            var id = Text(package, "id");
            var current = Text(package, "resolvedVersion");
            var latest = Text(package, "latestVersion");
            if (id is null || current is null || latest is null || current == latest)
            {
                continue;
            }

            found.TryAdd((id, current, project), new OutdatedPackage(Ecosystem, id, current, latest, project));
        }

        return [.. found.Values];
    }

    private static IEnumerable<(string? Project, JsonElement Package)> Packages(string json, bool includeTransitive)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("expected a 'projects' array");
        }

        // JsonElements stay valid only while the document lives, so results are materialised before it is disposed.
        var result = new List<(string?, JsonElement)>();
        foreach (var project in projects.EnumerateArray())
        {
            var name = ProjectName(Text(project, "path"));
            if (!project.TryGetProperty("frameworks", out var frameworks) || frameworks.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var framework in frameworks.EnumerateArray())
            {
                foreach (var list in includeTransitive ? new[] { "topLevelPackages", "transitivePackages" } : ["topLevelPackages"])
                {
                    if (framework.TryGetProperty(list, out var packages) && packages.ValueKind == JsonValueKind.Array)
                    {
                        result.AddRange(packages.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object).Select(p => (name, p.Clone())));
                    }
                }
            }
        }

        return result;
    }

    private static string? ProjectName(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var file = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        var dot = file.LastIndexOf('.');
        return dot > 0 ? file[..dot] : file;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
