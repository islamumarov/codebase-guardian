namespace CodebaseGuardian.Tests.Infrastructure;

/// <summary>
/// <see cref="TempGitRepo"/> must commit the same thing on every machine. Each test simulates a developer machine whose
/// user-level Git files or <c>GIT_*</c> variables would change the outcome, and checks that they have no effect.
/// </summary>
public class TempGitRepoIsolationTests
{
    /// <summary>Where git looks for the user-level configuration, ignore and attributes files.</summary>
    public enum UserLevelLocation
    {
        /// <summary><c>~/.gitconfig</c> and <c>~/.config/git/*</c>, used while <c>XDG_CONFIG_HOME</c> is not set.</summary>
        HomeDirectory,

        /// <summary><c>$XDG_CONFIG_HOME/git/*</c>.</summary>
        XdgConfigHome,
    }

    [Theory]
    [InlineData(UserLevelLocation.HomeDirectory)]
    [InlineData(UserLevelLocation.XdgConfigHome)]
    public void Files_matching_a_user_level_ignore_are_still_committed(UserLevelLocation location)
    {
        using var machine = new DeveloperMachine();
        using var repo = TempGitRepo.CreateWithAmbientEnvironment(machine.AmbientEnvironment(location));
        repo.WriteFile(".env", "KEY=value");
        repo.WriteFile("a.secret", "token");
        repo.WriteFile("ok.txt", "ok");

        repo.Commit("add files");

        Assert.Equal([".env", "a.secret", "ok.txt"], repo.Git("ls-files").Split('\n'));
    }

    [Theory]
    [InlineData(UserLevelLocation.HomeDirectory)]
    [InlineData(UserLevelLocation.XdgConfigHome)]
    public void User_level_attributes_do_not_change_what_is_committed(UserLevelLocation location)
    {
        using var machine = new DeveloperMachine();
        using var repo = TempGitRepo.CreateWithAmbientEnvironment(machine.AmbientEnvironment(location));
        repo.WriteFile("windows.crlf", "one\r\ntwo\r\n");

        repo.Commit("add a file with Windows line endings");

        // "*.crlf text" in a user-level attributes file would normalize the line endings and store 8 bytes.
        Assert.Equal("10", repo.Git("cat-file", "-s", "HEAD:windows.crlf"));
    }

    [Theory]
    [InlineData(UserLevelLocation.HomeDirectory)]
    [InlineData(UserLevelLocation.XdgConfigHome)]
    public void User_level_configuration_is_not_read(UserLevelLocation location)
    {
        using var machine = new DeveloperMachine();
        using var repo = TempGitRepo.CreateWithAmbientEnvironment(machine.AmbientEnvironment(location));

        Assert.Equal(["local"], ConfigScopes(repo));
    }

    [Fact]
    public void Identity_and_configuration_from_environment_variables_are_ignored()
    {
        using var repo = TempGitRepo.CreateWithAmbientEnvironment(new Dictionary<string, string?>
        {
            ["GIT_AUTHOR_NAME"] = "Env Author",
            ["GIT_AUTHOR_EMAIL"] = "env-author@example.org",
            ["GIT_COMMITTER_NAME"] = "Env Committer",
            ["GIT_COMMITTER_EMAIL"] = "env-committer@example.org",
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "user.name",
            ["GIT_CONFIG_VALUE_0"] = "Env Config Name",
            ["GIT_CONFIG_PARAMETERS"] = "'user.email=env-parameters@example.org'",
        });
        repo.WriteFile("a.txt", "a");

        repo.Commit("add a");

        var expected = $"{TempGitRepo.AuthorName} <{TempGitRepo.AuthorEmail}>";
        Assert.Equal(expected, repo.Git("log", "-1", "--format=%an <%ae>"));
        Assert.Equal(expected, repo.Git("log", "-1", "--format=%cn <%ce>"));
        Assert.Equal(["local"], ConfigScopes(repo)); // injected settings would show up with the scope "command"
    }

    [Fact]
    public void A_template_directory_from_the_environment_is_not_used()
    {
        using var machine = new DeveloperMachine();
        using var repo = TempGitRepo.CreateWithAmbientEnvironment(
            new Dictionary<string, string?> { ["GIT_TEMPLATE_DIR"] = machine.TemplateDirectory });
        repo.WriteFile("ok.txt", "ok");
        repo.WriteFile("x.templated", "x");

        repo.Commit("add files");

        // The template's info/exclude would hide the second file.
        Assert.Equal(["ok.txt", "x.templated"], repo.Git("ls-files").Split('\n'));
    }

    [Fact]
    public void No_template_files_are_copied_into_the_repository()
    {
        using var repo = TempGitRepo.Create();

        // Git's default template (sample hooks, info/exclude, ...) differs from machine to machine and may contain real hooks.
        Assert.False(Directory.Exists(Path.Combine(repo.Path, ".git", "hooks")));
        Assert.False(Directory.Exists(Path.Combine(repo.Path, ".git", "info")));
    }

    [Fact]
    public void The_configuration_of_the_machine_running_the_tests_is_not_read()
    {
        using var repo = TempGitRepo.Create();

        Assert.Equal(["local"], ConfigScopes(repo));
    }

    [Fact]
    public void Git_runs_with_an_isolated_environment()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The environment is read through a POSIX shell alias.");

        using var machine = new DeveloperMachine();
        var ambient = machine.AmbientEnvironment(UserLevelLocation.XdgConfigHome);
        ambient["GIT_AUTHOR_NAME"] = "Env Author";
        ambient["GIT_TEMPLATE_DIR"] = machine.TemplateDirectory;
        ambient["GIT_SOME_FUTURE_SETTING"] = "1"; // unknown to git today: only dropping every GIT_* variable removes it
        ambient["LC_ALL"] = "de_DE.UTF-8";
        using var repo = TempGitRepo.CreateWithAmbientEnvironment(ambient);

        var environment = GitEnvironment(repo);

        var leaked = ambient.Keys.Where(name => name.StartsWith("GIT_", StringComparison.Ordinal) && environment.ContainsKey(name));
        Assert.Empty(leaked);
        Assert.Equal("1", environment["GIT_CONFIG_NOSYSTEM"]);
        Assert.Equal("C", environment["LC_ALL"]); // git's messages stay in English

        // HOME, XDG_CONFIG_HOME and USERPROFILE all point at one empty directory that belongs to the repository.
        var home = environment["HOME"];
        Assert.Equal(home, environment["XDG_CONFIG_HOME"]);
        Assert.Equal(home, environment["USERPROFILE"]);
        Assert.True(Directory.Exists(home));
        Assert.Empty(Directory.GetFileSystemEntries(home));
        Assert.False(home.StartsWith(repo.Path + Path.DirectorySeparatorChar, StringComparison.Ordinal));

        repo.Dispose();
        Assert.False(Directory.Exists(home));
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    /// <summary>The distinct scopes (local, global, system, command, ...) of every configuration entry git can see.</summary>
    private static string[] ConfigScopes(TempGitRepo repo) =>
        [.. repo.Git("config", "--list", "--show-scope").Split('\n').Select(line => line.Split('\t')[0]).Distinct()];

    /// <summary>The environment variables git itself runs with, as seen by a shell alias.</summary>
    private static Dictionary<string, string> GitEnvironment(TempGitRepo repo)
    {
        repo.Git("config", "alias.dumpenv", "!env");

        var variables = new Dictionary<string, string>();
        foreach (var line in repo.Git("dumpenv").Split('\n'))
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                variables[line[..separator]] = line[(separator + 1)..];
            }
        }

        return variables;
    }

    /// <summary>
    /// The files a developer typically has: a global config, ignore and attributes file under <c>~</c> and, separately,
    /// under <c>$XDG_CONFIG_HOME</c>, plus a Git template directory.
    /// </summary>
    private sealed class DeveloperMachine : IDisposable
    {
        private const string Config = "[core]\n\tautocrlf = true\n[user]\n\tname = Global Name\n\temail = global@example.org\n";
        private const string Ignore = "*.secret\n.env\n";
        private const string Attributes = "*.crlf text\n";

        private readonly string _root = Path.Combine(Path.GetTempPath(), $"guardian-machine-{Guid.NewGuid():N}");

        public DeveloperMachine()
        {
            Write("home/.gitconfig", Config);
            Write("home/.config/git/ignore", Ignore);
            Write("home/.config/git/attributes", Attributes);

            Write("xdg/git/config", Config);
            Write("xdg/git/ignore", Ignore);
            Write("xdg/git/attributes", Attributes);

            Write("template/info/exclude", "*.templated\n");
            Directory.CreateDirectory(Path.Combine(_root, "elsewhere"));
        }

        public string TemplateDirectory => Path.Combine(_root, "template");

        /// <summary>The variables that make git find the user-level files at <paramref name="location"/>.</summary>
        public Dictionary<string, string?> AmbientEnvironment(UserLevelLocation location)
        {
            var home = Path.Combine(_root, location == UserLevelLocation.HomeDirectory ? "home" : "elsewhere");
            return new Dictionary<string, string?>
            {
                ["HOME"] = home,
                ["USERPROFILE"] = home,
                ["XDG_CONFIG_HOME"] = location == UserLevelLocation.XdgConfigHome ? Path.Combine(_root, "xdg") : null,
            };
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Leaving a temporary directory behind is harmless.
            }
        }

        private void Write(string relativePath, string content)
        {
            var fullPath = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }
    }
}
