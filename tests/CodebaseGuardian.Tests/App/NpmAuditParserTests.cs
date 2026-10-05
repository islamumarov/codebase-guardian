using System.Text.Json;
using CodebaseGuardian.Dependencies;

namespace CodebaseGuardian.Tests.App;

public class NpmAuditParserTests
{
    internal const string AuditJson = """
        {
          "auditReportVersion": 2,
          "vulnerabilities": {
            "lodash": {
              "name": "lodash",
              "severity": "high",
              "isDirect": true,
              "via": [
                { "source": 1106913, "name": "lodash", "title": "Command Injection", "url": "https://github.com/advisories/GHSA-35jh-r3h4-6jhm", "severity": "high", "range": "<4.17.21" }
              ],
              "effects": [],
              "range": "<4.17.21",
              "nodes": ["node_modules/lodash"],
              "fixAvailable": true
            },
            "express": {
              "name": "express",
              "severity": "moderate",
              "isDirect": true,
              "via": ["lodash"],
              "effects": [],
              "range": "4.0.0 - 4.17.2",
              "nodes": ["node_modules/express"],
              "fixAvailable": { "name": "express", "version": "4.21.0", "isSemVerMajor": false }
            }
          },
          "metadata": { "vulnerabilities": { "total": 2 } }
        }
        """;

    internal const string OutdatedJson = """
        {
          "left-pad": { "current": "1.1.0", "wanted": "1.1.3", "latest": "1.3.0", "dependent": "app", "location": "node_modules/left-pad" },
          "missing-pkg": { "wanted": "1.0.0", "latest": "2.0.0", "dependent": "app" }
        }
        """;

    [Fact]
    public void Audit_reports_name_severity_range_and_the_advisory_url()
    {
        var found = NpmAuditParser.ParseAudit(AuditJson);

        Assert.Equal(2, found.Count);
        Assert.Equal(new VulnerablePackage("npm", "lodash", "<4.17.21", "high",
            "https://github.com/advisories/GHSA-35jh-r3h4-6jhm", null), Assert.Single(found, p => p.Package == "lodash"));
        var express = Assert.Single(found, p => p.Package == "express");
        Assert.Equal("moderate", express.Severity);
        Assert.Null(express.AdvisoryUrl);
    }

    [Fact]
    public void Audit_without_a_vulnerabilities_object_is_an_error_with_npms_message()
    {
        var error = Assert.Throws<JsonException>(() =>
            NpmAuditParser.ParseAudit("""{ "error": { "code": "ENOLOCK", "summary": "This command requires an existing lockfile." } }"""));

        Assert.Contains("requires an existing lockfile", error.Message);
    }

    [Fact]
    public void Outdated_reports_current_and_latest_and_uses_not_installed_without_a_current_version()
    {
        var found = NpmAuditParser.ParseOutdated(OutdatedJson);

        Assert.Equal(new OutdatedPackage("npm", "left-pad", "1.1.0", "1.3.0", null), found[0]);
        Assert.Equal("(not installed)", found[1].Current);
    }

    [Fact]
    public void Empty_outdated_output_means_nothing_is_outdated()
    {
        Assert.Empty(NpmAuditParser.ParseOutdated(""));
        Assert.Empty(NpmAuditParser.ParseOutdated("{}"));
    }
}
