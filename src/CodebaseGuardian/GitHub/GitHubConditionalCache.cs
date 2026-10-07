namespace CodebaseGuardian.GitHub;

/// <summary>Thread-safe path -> (ETag, body, next link) store; one instance per long-lived client.</summary>
internal sealed class GitHubConditionalCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string ETag, string Body, string? NextLink)> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string path, out (string ETag, string Body, string? NextLink) entry)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(path, out entry);
        }
    }

    public void Set(string path, string etag, string body, string? nextLink)
    {
        lock (_gate)
        {
            _entries[path] = (etag, body, nextLink);
        }
    }
}
