using System.Text.Json;
using CodebaseGuardian.Git;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.Checks;

public sealed record CheckCommand(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>The command line as shown to people, for example <c>dotnet test CodebaseGuardian.slnx --nologo</c>.</summary>
    public string Display => string.Join(' ', [FileName, .. Arguments]);
}

public interface ICheckCommandResolver
{
    /// <summary>The command that checks the repository, or <c>null</c> when none is configured or detected.</summary>
    CheckCommand? Resolve();
}

public sealed class CheckCommandResolver(IGitRepository git, IOptions<CheckOptions> options) : ICheckCommandResolver
{
    public CheckCommand? Resolve()
    {
        var configured = options.Value;
        if (!string.IsNullOrWhiteSpace(configured.Command))
        {
            return new CheckCommand(configured.Command, configured.Arguments);
        }

        var root = git.RootPath;
        var solution = FirstFile(root, "*.slnx") ?? FirstFile(root, "*.sln");
        if (solution is not null)
        {
            return new CheckCommand("dotnet", ["test", solution, "--nologo"]);
        }

        if (Directory.GetFiles(root, "*.csproj").Length == 1)
        {
            return new CheckCommand("dotnet", ["test", "--nologo"]);
        }

        return HasNpmTestScript(Path.Combine(root, "package.json")) ? new CheckCommand("npm", ["test"]) : null;
    }

    private static string? FirstFile(string root, string pattern) =>
        Directory.GetFiles(root, pattern).Select(Path.GetFileName).Order(StringComparer.Ordinal).FirstOrDefault();

    private static bool HasNpmTestScript(string packageJson)
    {
        if (!File.Exists(packageJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJson));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("scripts", out var scripts)
                && scripts.ValueKind == JsonValueKind.Object
                && scripts.TryGetProperty("test", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
