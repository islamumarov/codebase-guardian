using System.Text.Json;
using CodebaseGuardian.Dependencies;

namespace CodebaseGuardian.Tests.App;

public class NuGetAuditParserTests
{
    internal const string VulnerableJson = """
        {
          "version": 1,
          "parameters": "--vulnerable --include-transitive",
          "projects": [
            {
              "path": "/repo/src/Web/Web.csproj",
              "frameworks": [
                {
                  "framework": "net10.0",
                  "topLevelPackages": [
                    {
                      "id": "Newtonsoft.Json",
                      "requestedVersion": "12.0.1",
                      "resolvedVersion": "12.0.1",
                      "vulnerabilities": [
                        { "severity": "High", "advisoryurl": "https://github.com/advisories/GHSA-5crp-9r3c-p9vr" }
                      ]
                    }
                  ],
                  "transitivePackages": [
                    {
                      "id": "System.Text.Encodings.Web",
                      "resolvedVersion": "4.5.0",
                      "vulnerabilities": [
                        { "severity": "Critical", "advisoryurl": "https://github.com/advisories/GHSA-ghhp-997w-qr28" }
                      ]
                    }
                  ]
                },
                {
                  "framework": "net9.0",
                  "topLevelPackages": [
                    {
                      "id": "Newtonsoft.Json",
                      "requestedVersion": "12.0.1",
                      "resolvedVersion": "12.0.1",
                      "vulnerabilities": [
                        { "severity": "High", "advisoryurl": "https://github.com/advisories/GHSA-5crp-9r3c-p9vr" }
                      ]
                    }
                  ]
                }
              ]
            },
            { "path": "/repo/src/Lib/Lib.csproj" }
          ]
        }
        """;

    internal const string OutdatedJson = """
        {
          "version": 1,
          "parameters": "--outdated",
          "projects": [
            {
              "path": "/repo/src/Web/Web.csproj",
              "frameworks": [
                {
                  "framework": "net10.0",
                  "topLevelPackages": [
                    { "id": "Serilog", "requestedVersion": "2.0.0", "resolvedVersion": "2.0.0", "latestVersion": "4.2.0" },
                    { "id": "Polly", "requestedVersion": "8.5.0", "resolvedVersion": "8.5.0", "latestVersion": "8.5.0" }
                  ]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Vulnerable_top_level_and_transitive_packages_carry_project_and_severity()
    {
        var found = NuGetAuditParser.ParseVulnerable(VulnerableJson);

        Assert.Equal(2, found.Count);
        var top = Assert.Single(found, p => p.Package == "Newtonsoft.Json");
        Assert.Equal(new VulnerablePackage("nuget", "Newtonsoft.Json", "12.0.1", "High",
            "https://github.com/advisories/GHSA-5crp-9r3c-p9vr", "Web"), top);
        var transitive = Assert.Single(found, p => p.Package == "System.Text.Encodings.Web");
        Assert.Equal("Critical", transitive.Severity);
        Assert.Equal("4.5.0", transitive.Version);
    }

    [Fact]
    public void The_same_package_in_several_frameworks_is_reported_once()
    {
        Assert.Single(NuGetAuditParser.ParseVulnerable(VulnerableJson), p => p.Package == "Newtonsoft.Json");
    }

    [Fact]
    public void Outdated_packages_report_current_and_latest_and_skip_up_to_date_ones()
    {
        var found = NuGetAuditParser.ParseOutdated(OutdatedJson);

        Assert.Equal(new OutdatedPackage("nuget", "Serilog", "2.0.0", "4.2.0", "Web"), Assert.Single(found));
    }

    [Fact]
    public void Output_that_is_not_json_throws_a_json_exception()
    {
        Assert.ThrowsAny<JsonException>(() => NuGetAuditParser.ParseVulnerable("Unable to load the project"));
    }
}
