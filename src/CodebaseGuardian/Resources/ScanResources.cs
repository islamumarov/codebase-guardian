using System.ComponentModel;
using CodebaseGuardian.Scanning;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodebaseGuardian.Resources;

[McpServerResourceType]
public sealed class ScanResources(ScanReportStore store)
{
    [McpServerResource(UriTemplate = "guardian://scans/{scanId}/report", Name = "scan-report", MimeType = "text/markdown")]
    [Description("The Markdown report of one full_scan: secrets (redacted), vulnerable and outdated dependencies, the check run and any step errors. The scan id comes from full_scan or the scan.completed event; the last 20 reports are kept.")]
    public string Report(string scanId) =>
        store.Get(scanId)?.Markdown ?? throw new McpProtocolException($"Unknown scan '{scanId}'.", McpErrorCode.InvalidParams);
}
