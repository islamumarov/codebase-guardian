using CodebaseGuardian.Dependencies;

namespace CodebaseGuardian.Tests.App;

public class DependencyManifestsTests
{
    [Theory]
    [InlineData("src/App/App.csproj", "nuget")]
    [InlineData("Lib.fsproj", "nuget")]
    [InlineData("Lib.vbproj", "nuget")]
    [InlineData("Directory.Packages.props", "nuget")]
    [InlineData("src/Directory.Build.props", "nuget")]
    [InlineData("src/App/packages.lock.json", "nuget")]
    [InlineData("global.json", "nuget")]
    [InlineData("package.json", "npm")]
    [InlineData("web/package-lock.json", "npm")]
    [InlineData("npm-shrinkwrap.json", "npm")]
    [InlineData("yarn.lock", "npm")]
    [InlineData("apps\\web\\pnpm-lock.yaml", "npm")]
    [InlineData("SRC/APP.CSPROJ", "nuget")]
    [InlineData("README.md", null)]
    [InlineData("src/Program.cs", null)]
    [InlineData("package.json.bak", null)]
    [InlineData("my-package.json", null)]
    [InlineData("tsconfig.json", null)]
    [InlineData("", null)]
    public void Ecosystem_is_detected_from_the_file_name(string path, string? ecosystem)
    {
        Assert.Equal(ecosystem, DependencyManifests.GetEcosystem(path));
        Assert.Equal(ecosystem is not null, DependencyManifests.IsManifest(path));
    }
}
