using System.Text.Json;
using CodebaseGuardian.Git;
using CodebaseGuardian.Processes;

namespace CodebaseGuardian.Dependencies;

public sealed class DependencyAuditor(IProcessRunner processes, IGitRepository git) : IDependencyAuditor
{
    private const int MaxReasonChars = 500;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    public async Task<DependencyAuditReport> AuditAsync(bool includeOutdated, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var root = git.RootPath;
        // The file list can include tracked files that were deleted from the working tree.
        var present = (await git.ListFilesAsync(cancellationToken))
            .Where(path => File.Exists(Path.Combine(root, path)))
            .Select(DependencyManifests.GetEcosystem)
            .Where(ecosystem => ecosystem is not null)
            .ToHashSet();

        var reports = new List<EcosystemReport>();
        if (present.Contains("nuget"))
        {
            progress?.Report("auditing nuget");
            reports.Add(await AuditEcosystemAsync("nuget", "dotnet", () => AuditNuGetAsync(root, includeOutdated, cancellationToken)));
        }

        if (present.Contains("npm"))
        {
            progress?.Report("auditing npm");
            reports.Add(await AuditEcosystemAsync("npm", "npm", () => AuditNpmAsync(root, includeOutdated, cancellationToken)));
        }

        return new DependencyAuditReport(reports);
    }

    private static async Task<EcosystemReport> AuditEcosystemAsync(string ecosystem, string tool, Func<Task<EcosystemReport>> audit)
    {
        try
        {
            return await audit();
        }
        catch (ExecutableNotFoundException)
        {
            return EcosystemReport.Skipped(ecosystem, $"{tool} not found on PATH");
        }
        catch (JsonException exception)
        {
            return EcosystemReport.Failed(ecosystem, Truncate($"could not parse {tool} output: {exception.Message}"));
        }
        catch (CommandFailedException exception)
        {
            return EcosystemReport.Failed(ecosystem, exception.Message);
        }
    }

    private async Task<EcosystemReport> AuditNuGetAsync(string root, bool includeOutdated, CancellationToken ct)
    {
        var target = Directory.GetFiles(root, "*.slnx").Concat(Directory.GetFiles(root, "*.sln"))
            .Select(Path.GetFileName).OfType<string>().OrderBy(n => Path.GetExtension(n) == ".slnx" ? 0 : 1).ThenBy(n => n, StringComparer.Ordinal)
            .FirstOrDefault() ?? root;

        var vulnerable = NuGetAuditParser.ParseVulnerable(await RunAsync(
            root, "dotnet", ["list", target, "package", "--vulnerable", "--include-transitive", "--format", "json", "--output-version", "1"], [0], ct));
        return await WithOutdatedAsync("nuget", "dotnet", vulnerable, includeOutdated, async () => NuGetAuditParser.ParseOutdated(await RunAsync(
            root, "dotnet", ["list", target, "package", "--outdated", "--format", "json", "--output-version", "1"], [0], ct)));
    }

    private async Task<EcosystemReport> AuditNpmAsync(string root, bool includeOutdated, CancellationToken ct)
    {
        // npm exits 1 when it found vulnerabilities or outdated packages; that is a result, not a failure.
        var vulnerable = NpmAuditParser.ParseAudit(await RunAsync(root, "npm", ["audit", "--json"], [0, 1], ct));
        return await WithOutdatedAsync("npm", "npm", vulnerable, includeOutdated, async () =>
            NpmAuditParser.ParseOutdated(await RunAsync(root, "npm", ["outdated", "--json"], [0, 1], ct)));
    }

    // A failing outdated check must not discard the vulnerabilities that were already found.
    private static async Task<EcosystemReport> WithOutdatedAsync(
        string ecosystem, string tool, IReadOnlyList<VulnerablePackage> vulnerable, bool includeOutdated,
        Func<Task<IReadOnlyList<OutdatedPackage>>> outdatedCheck)
    {
        if (!includeOutdated)
        {
            return new EcosystemReport(ecosystem, "ok", null, vulnerable, []);
        }

        try
        {
            return new EcosystemReport(ecosystem, "ok", null, vulnerable, await outdatedCheck());
        }
        catch (JsonException exception)
        {
            return Partial($"could not parse {tool} outdated output: {exception.Message}");
        }
        catch (CommandFailedException exception)
        {
            return Partial($"outdated check failed: {exception.Message}");
        }

        EcosystemReport Partial(string reason) => new(ecosystem, "failed", Truncate(reason), vulnerable, []);
    }

    private async Task<string> RunAsync(string root, string fileName, string[] arguments, int[] acceptedExitCodes, CancellationToken ct)
    {
        var result = await processes.RunAsync(new ProcessSpec(fileName, arguments, root) { Timeout = CommandTimeout }, ct);
        if (result.TimedOut)
        {
            throw new CommandFailedException($"{fileName} timed out after {CommandTimeout.TotalMinutes:0} minutes");
        }

        if (!acceptedExitCodes.Contains(result.ExitCode))
        {
            var detail = (string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError).Trim();
            throw new CommandFailedException(Truncate($"{fileName} exited with exit code {result.ExitCode}: {detail}"));
        }

        return result.StandardOutput;
    }

    private static string Truncate(string text) => text.Length <= MaxReasonChars ? text : text[..MaxReasonChars] + "...";

    private sealed class CommandFailedException(string message) : Exception(message);
}
