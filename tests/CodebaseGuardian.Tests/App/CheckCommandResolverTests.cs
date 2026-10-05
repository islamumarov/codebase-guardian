using CodebaseGuardian.Checks;
using CodebaseGuardian.Hosting;
using CodebaseGuardian.Git;
using CodebaseGuardian.Processes;
using CodebaseGuardian.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.Tests.App;

public class CheckCommandResolverTests
{
    private static CheckCommand? Resolve(TempGitRepo repo, CheckOptions? options = null)
    {
        var git = new GitRepository(new ProcessRunner(), Options.Create(new GuardianOptions { RepositoryPath = repo.Path }));
        return new CheckCommandResolver(git, Options.Create(options ?? new CheckOptions())).Resolve();
    }

    [Fact]
    public void Solution_slnx_is_preferred_over_sln()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("B.sln", "");
        repo.WriteFile("A.slnx", "");
        repo.WriteFile("C.csproj", "");

        var command = Resolve(repo)!;

        Assert.Equal("dotnet", command.FileName);
        Assert.Equal(["test", "A.slnx", "--nologo"], command.Arguments);
        Assert.Equal("dotnet test A.slnx --nologo", command.Display);
    }

    [Fact]
    public void Solution_sln_is_used_when_there_is_no_slnx()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("B.sln", "");
        repo.WriteFile("A.sln", "");

        Assert.Equal("dotnet test A.sln --nologo", Resolve(repo)!.Display);
    }

    [Fact]
    public void A_single_csproj_is_tested_without_a_file_argument()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("App.csproj", "");

        Assert.Equal("dotnet test --nologo", Resolve(repo)!.Display);
    }

    [Fact]
    public void Several_csproj_files_without_a_solution_are_ambiguous()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("A.csproj", "");
        repo.WriteFile("B.csproj", "");

        Assert.Null(Resolve(repo));
    }

    [Fact]
    public void Package_json_with_a_test_script_runs_npm_test()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("package.json", """{"scripts":{"test":"jest"}}""");

        var command = Resolve(repo)!;

        Assert.Equal("npm", command.FileName);
        Assert.Equal(["test"], command.Arguments);
        Assert.Equal("npm test", command.Display);
    }

    [Fact]
    public void Package_json_without_a_test_script_is_not_a_check_command()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("package.json", """{"scripts":{"build":"tsc"}}""");

        Assert.Null(Resolve(repo));
    }

    [Fact]
    public void Nothing_detectable_resolves_to_null()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("README.md", "x");

        Assert.Null(Resolve(repo));
    }

    [Fact]
    public void A_configured_command_wins_over_detection()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("A.slnx", "");

        var command = Resolve(repo, new CheckOptions { Command = "make", Arguments = ["check", "-j4"] })!;

        Assert.Equal("make", command.FileName);
        Assert.Equal(["check", "-j4"], command.Arguments);
        Assert.Equal("make check -j4", command.Display);
    }
}
