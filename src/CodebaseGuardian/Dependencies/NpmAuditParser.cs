using System.Text.Json;

namespace CodebaseGuardian.Dependencies;

/// <summary>Parses <c>npm audit --json</c> and <c>npm outdated --json</c>.</summary>
public static class NpmAuditParser
{
    private const string Ecosystem = "npm";

    /// <exception cref="JsonException">The text is not an audit report (including npm's own error object).</exception>
    public static IReadOnlyList<VulnerablePackage> ParseAudit(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("expected a JSON object");
        }

        if (!root.TryGetProperty("vulnerabilities", out var vulnerabilities) || vulnerabilities.ValueKind != JsonValueKind.Object)
        {
            var summary = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                ? Text(error, "summary") ?? Text(error, "code")
                : null;
            throw new JsonException(summary is null ? "no 'vulnerabilities' object in the audit report" : $"npm audit reported an error: {summary}");
        }

        var found = new List<VulnerablePackage>();
        foreach (var entry in vulnerabilities.EnumerateObject().Where(e => e.Value.ValueKind == JsonValueKind.Object))
        {
            found.Add(new VulnerablePackage(
                Ecosystem, entry.Name, Text(entry.Value, "range") ?? "unknown", Text(entry.Value, "severity") ?? "unknown",
                AdvisoryUrl(entry.Value), null));
        }

        return found;
    }

    /// <exception cref="JsonException">The text is not JSON.</exception>
    public static IReadOnlyList<OutdatedPackage> ParseOutdated(string json)
    {
        // npm prints nothing at all when everything is up to date.
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("expected a JSON object");
        }

        var found = new List<OutdatedPackage>();
        foreach (var entry in document.RootElement.EnumerateObject().Where(e => e.Value.ValueKind == JsonValueKind.Object))
        {
            var latest = Text(entry.Value, "latest");
            if (latest is not null)
            {
                found.Add(new OutdatedPackage(Ecosystem, entry.Name, Text(entry.Value, "current") ?? "(not installed)", latest, null));
            }
        }

        return found;
    }

    private static string? AdvisoryUrl(JsonElement vulnerability) =>
        vulnerability.TryGetProperty("via", out var via) && via.ValueKind == JsonValueKind.Array
            ? via.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Object).Select(v => Text(v, "url")).FirstOrDefault(u => u is not null)
            : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
