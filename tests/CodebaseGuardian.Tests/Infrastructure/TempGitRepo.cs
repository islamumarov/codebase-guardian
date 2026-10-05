using System.Diagnostics;
using IoPath = System.IO.Path;

namespace CodebaseGuardian.Tests.Infrastructure;

/// <summary>
/// A throwaway Git repository in a temporary directory, driven through the <c>git</c> CLI.
/// </summary>
/// <remarks>
/// Git is isolated from the machine it runs on, so what a test commits does not depend on whose machine that is. It sees no
/// user-level or system-level configuration, ignore or attributes files (<c>HOME</c>, <c>XDG_CONFIG_HOME</c> and
/// <c>USERPROFILE</c> point at an empty directory that belongs to the repository, and system configuration is skipped), none
/// of the machine's <c>GIT_*</c> environment variables (identity, injected configuration, template directory,
/// <c>GIT_DIR</c> when the tests run from a Git hook, ...), and no template directory; its messages stay in English.
/// Not isolated: the git executable itself, its version, and <c>PATH</c>. Code under test that starts git on its own,
/// for example through the application's process runner, is not isolated either.
/// </remarks>
public sealed class TempGitRepo : IDisposable
{
    public const string AuthorName = "Guardian Tests";
    public const string AuthorEmail = "guardian-tests@example.com";

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    private readonly string _home;
    private readonly IReadOnlyDictionary<string, string?> _ambientEnvironment;

    private TempGitRepo(string path, string home, IReadOnlyDictionary<string, string?> ambientEnvironment)
    {
        Path = path;
        _home = home;
        _ambientEnvironment = ambientEnvironment;
    }

    /// <summary>Full path of the repository's working directory.</summary>
    public string Path { get; }

    /// <summary>Creates a repository on branch <c>main</c> with a test identity and unsigned commits.</summary>
    public static TempGitRepo Create() => CreateWithAmbientEnvironment(new Dictionary<string, string?>());

    /// <summary>
    /// Like <see cref="Create"/>, but first adds <paramref name="ambientEnvironment"/> to the environment that git would inherit
    /// from the test process (a <c>null</c> value removes the variable). This simulates a developer machine, for example
    /// one with a global ignore file or <c>GIT_AUTHOR_NAME</c> set, without touching the test process's own environment.
    /// </summary>
    internal static TempGitRepo CreateWithAmbientEnvironment(IReadOnlyDictionary<string, string?> ambientEnvironment)
    {
        var id = Guid.NewGuid().ToString("N");
        var repo = new TempGitRepo(
            IoPath.Combine(IoPath.GetTempPath(), $"guardian-repo-{id}"),
            IoPath.Combine(IoPath.GetTempPath(), $"guardian-repo-{id}-home"), // outside the working tree, so it is never committed
            ambientEnvironment);
        try
        {
            Directory.CreateDirectory(repo.Path);
            Directory.CreateDirectory(repo._home);

            // The empty template keeps the machine's template directory (sample or real hooks, info/exclude, ...) out of .git.
            repo.Git("init", "-b", "main", "--template=");
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

    /// <summary>Deletes the repository and its isolated home directory. Best effort: a locked temporary directory must not fail a test.</summary>
    public void Dispose()
    {
        DeleteDirectory(Path);
        DeleteDirectory(_home);
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal); // Git stores objects read-only, which blocks deletion on Windows.
            }

            Directory.Delete(path, recursive: true);
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

        var environment = startInfo.Environment;
        foreach (var (name, value) in _ambientEnvironment)
        {
            if (value is null)
            {
                environment.Remove(name);
            }
            else
            {
                environment[name] = value;
            }
        }

        // Git is steered by GIT_* variables (author and committer, injected configuration, template directory, GIT_DIR, ...)
        // and new ones appear with new versions: none of the machine's reaches it, whatever its name. This prefix drop also
        // covers command-scope config (GIT_CONFIG_COUNT/KEY_n/VALUE_n, GIT_CONFIG_PARAMETERS), which would outrank local config.
        foreach (var name in environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            environment.Remove(name);
        }

        // Git reads the user-level configuration, ignore and attributes files from under HOME and XDG_CONFIG_HOME; an empty
        // directory has none, and unlike GIT_CONFIG_GLOBAL this works for every git version and every kind of file.
        environment["HOME"] = _home;
        environment["XDG_CONFIG_HOME"] = _home;
        environment["USERPROFILE"] = _home;
        environment["GIT_CONFIG_NOSYSTEM"] = "1"; // no system-level configuration either
        environment["GIT_TERMINAL_PROMPT"] = "0";
        environment["LC_ALL"] = "C"; // git's messages stay in English
        return startInfo;
    }
}
