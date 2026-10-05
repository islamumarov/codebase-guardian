using System.Diagnostics;
using IoPath = System.IO.Path;

namespace CodebaseGuardian.Tests.Infrastructure;

/// <summary>
/// A throwaway Git repository in a temporary directory, driven through the <c>git</c> CLI.
/// </summary>
/// <remarks>
/// Git runs hermetically: the developer's global and system configuration (excludes, autocrlf, hooks, signing, ...) and any
/// inherited <c>GIT_DIR</c>-style variables (set when the tests run from a Git hook) never reach it, so every machine
/// commits exactly the same thing.
/// </remarks>
public sealed class TempGitRepo : IDisposable
{
    public const string AuthorName = "Guardian Tests";
    public const string AuthorEmail = "guardian-tests@example.com";

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    private static readonly string[] InheritedRepositoryVariables =
    [
        "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_COMMON_DIR", "GIT_PREFIX",
    ];

    private TempGitRepo(string path) => Path = path;

    /// <summary>Full path of the repository's working directory.</summary>
    public string Path { get; }

    /// <summary>Creates a repository on branch <c>main</c> with a test identity and unsigned commits.</summary>
    public static TempGitRepo Create()
    {
        var repo = new TempGitRepo(IoPath.Combine(IoPath.GetTempPath(), $"guardian-repo-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(repo.Path);
        try
        {
            repo.Git("init", "-b", "main");
            repo.Git("config", "user.name", AuthorName);
            repo.Git("config", "user.email", AuthorEmail);
            repo.Git("config", "commit.gpgsign", "false");
            return repo;
        }
        catch
        {
            repo.Dispose();
            throw;
        }
    }

    /// <summary>Writes a UTF-8 file (without byte-order mark), creating directories as needed.</summary>
    public void WriteFile(string relativePath, string content)
    {
        var fullPath = Resolve(relativePath);
        Directory.CreateDirectory(IoPath.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    public void DeleteFile(string relativePath)
    {
        var fullPath = Resolve(relativePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"'{relativePath}' does not exist in the temporary repository.", fullPath);
        }

        File.Delete(fullPath);
    }

    /// <summary>Stages everything (<c>git add -A</c>), commits it, and returns the full commit SHA.</summary>
    public string Commit(string message)
    {
        Git("add", "-A");
        Git("commit", "-m", message);
        return Git("rev-parse", "HEAD");
    }

    /// <summary>Runs <c>git</c> in the repository and returns its trimmed standard output; throws with standard error on failure.</summary>
    public string Git(params string[] args) => GitWithEnvironment(null, args);

    /// <summary>Stages everything and commits with the given committer date (any ISO 8601 offset), returning the full SHA.</summary>
    public string CommitAt(string message, string committerDate)
    {
        Git("add", "-A");
        GitWithEnvironment(new Dictionary<string, string> { ["GIT_COMMITTER_DATE"] = committerDate }, "commit", "-m", message);
        return Git("rev-parse", "HEAD");
    }

    private string GitWithEnvironment(IReadOnlyDictionary<string, string>? environment, params string[] args)
    {
        var startInfo = CreateStartInfo(args);
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The git process could not be started.");
        process.StandardInput.Close();

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(GitTimeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"git {string.Join(' ', args)} did not finish within {GitTimeout.TotalSeconds:0} seconds.");
        }

        // WaitForExit(timeout) returns when the process ends, not when the redirected streams are drained.
        var output = standardOutput.GetAwaiter().GetResult();
        var error = standardError.GetAwaiter().GetResult();

        return process.ExitCode == 0
            ? output.Trim()
            : throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed with exit code {process.ExitCode}: {error.Trim()}");
    }

    /// <summary>Deletes the repository directory. Best effort: a locked temporary directory must not fail a test.</summary>
    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal); // Git stores objects read-only, which blocks deletion on Windows.
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Leaving a temporary directory behind is harmless.
        }
    }

    private string Resolve(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var fullPath = IoPath.GetFullPath(relativePath, Path);
        var root = IoPath.TrimEndingDirectorySeparator(IoPath.GetFullPath(Path)) + IoPath.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{relativePath}' is outside the temporary repository.", nameof(relativePath));
        }

        return fullPath;
    }

    private ProcessStartInfo CreateStartInfo(IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = Path,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var variable in InheritedRepositoryVariables)
        {
            startInfo.Environment.Remove(variable);
        }

        // Command-scope config (GIT_CONFIG_COUNT/KEY_n/VALUE_n, GIT_CONFIG_PARAMETERS) outranks
        // local config, so it must not leak into the hermetic repo.
        foreach (var key in startInfo.Environment.Keys.ToList())
        {
            if (key.StartsWith("GIT_CONFIG_KEY_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("GIT_CONFIG_VALUE_", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.Environment.Remove(key);
            }
        }

        startInfo.Environment.Remove("GIT_CONFIG_COUNT");
        startInfo.Environment.Remove("GIT_CONFIG_PARAMETERS");

        startInfo.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        return startInfo;
    }
}
