using System.Text.RegularExpressions;

namespace CodebaseGuardian.Checks;

public sealed record ParsedTestOutput(string Summary, IReadOnlyList<string> FailedTests);

/// <summary>Best-effort reading of dotnet (VSTest and Microsoft.Testing.Platform) and npm output.</summary>
public static partial class TestOutputParser
{
    public const int MaxFailedTests = 50;

    private static readonly string[] SummaryMarkers = ["Passed!", "Failed!", "Test run summary", "total:", "failed:", "npm ERR!"];

    // "  Failed Ns.Class.Test [12 ms]" (VSTest) and "failed Ns.Class.Test (12ms)" (Microsoft.Testing.Platform).
    [GeneratedRegex(@"^\s*failed\s+(?<name>.+?)\s*(?:\[[^\]]*\d\s*m?s\]|\(\s*\d[^)]*m?s\))\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FailedTestLine();

    public static ParsedTestOutput Parse(string output, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(output);

        var failed = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? summary = null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (FailedTestLine().Match(line) is { Success: true } match)
            {
                var name = match.Groups["name"].Value;
                if (failed.Count < MaxFailedTests && seen.Add(name))
                {
                    failed.Add(name);
                }
            }

            if (SummaryMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                summary = line.Trim();
            }
        }

        return new ParsedTestOutput(summary ?? $"exit code {exitCode}", failed);
    }
}
