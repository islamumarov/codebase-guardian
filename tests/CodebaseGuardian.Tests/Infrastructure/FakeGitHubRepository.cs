using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using CodebaseGuardian.GitHub;

namespace CodebaseGuardian.Tests.Infrastructure;

/// <summary>
/// A mutable commit graph served through <see cref="FakeGitHubApi"/> as acme/widgets (or the given names). Everything
/// the REST endpoints answer is derived from the graph, so tests describe history once and exercise it end to end.
/// </summary>
public sealed class FakeGitHubRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly object _gate = new();
    private readonly Dictionary<string, Node> _commits = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, string> _branches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private int _order;
    private int _branchVersion;
    private int _branchListRequests;

    public FakeGitHubRepository(FakeGitHubApi api, string owner = "acme", string name = "widgets", string defaultBranch = "main")
    {
        DefaultBranch = defaultBranch;
        var root = $"/repos/{owner}/{name}";
        api.Map(HttpMethod.Get, root, (request, _) => ServeRepository(request))
            .Map(HttpMethod.Get, root + "/branches", (request, _) => ServeBranches(request))
            .Map(HttpMethod.Get, root + "/commits", (request, _) => ServeCommits(request))
            .MapPrefix(HttpMethod.Get, root + "/commits/", (request, _) => ServeCommit(request, root + "/commits/"))
            .MapPrefix(HttpMethod.Get, root + "/compare/", (request, _) => ServeCompare(request, root + "/compare/"))
            .MapPrefix(HttpMethod.Get, root + "/contents/", (request, _) => ServeContents(request, root + "/contents/"));
    }

    public string DefaultBranch { get; set; }

    /// <summary>How many GET .../branches reached the fake (304s and 409s included).</summary>
    public int BranchListRequests => Volatile.Read(ref _branchListRequests);

    /// <summary>Adds a commit; parents must exist. Returns this.</summary>
    public FakeGitHubRepository Commit(string sha, string message, IReadOnlyList<string> parents, params GitHubFileChange[] files)
    {
        lock (_gate)
        {
            foreach (var parent in parents)
            {
                if (!_commits.ContainsKey(parent))
                {
                    throw new ArgumentException($"Parent '{parent}' of commit '{sha}' does not exist.", nameof(parents));
                }
            }

            _commits[sha] = new Node(sha, message, [.. parents], [.. files], _order++);
        }

        return this;
    }

    /// <summary>Creates or moves a branch; changes the branches ETag.</summary>
    public FakeGitHubRepository SetBranch(string branch, string sha)
    {
        lock (_gate)
        {
            _branches[branch] = sha;
            _branchVersion++;
        }

        return this;
    }

    public FakeGitHubRepository DeleteBranch(string branch)
    {
        lock (_gate)
        {
            _branches.Remove(branch);
            _branchVersion++;
        }

        return this;
    }

    /// <summary>The commit now answers 404, as after garbage collection following a force-push.</summary>
    public FakeGitHubRepository Forget(string sha)
    {
        lock (_gate)
        {
            if (_commits.TryGetValue(sha, out var node))
            {
                node.Forgotten = true;
            }
        }

        return this;
    }

    /// <summary>A file served by the contents endpoint at every ref.</summary>
    public FakeGitHubRepository File(string path, string content)
    {
        lock (_gate)
        {
            _files[path] = content;
        }

        return this;
    }

    // ---- endpoints ----------------------------------------------------------------------------------------------

    private HttpResponseMessage ServeRepository(HttpRequestMessage request) =>
        WithEtag(request, $"\"repo-{DefaultBranch}\"", () => Ok(new { default_branch = DefaultBranch, @private = false }));

    private HttpResponseMessage ServeBranches(HttpRequestMessage request)
    {
        Interlocked.Increment(ref _branchListRequests);
        lock (_gate)
        {
            if (LiveCount() == 0)
            {
                return Conflict();
            }

            return WithEtag(request, $"\"b{_branchVersion}\"",
                () => Ok(_branches.Select(branch => new { name = branch.Key, commit = new { sha = branch.Value } })));
        }
    }

    private HttpResponseMessage ServeCommits(HttpRequestMessage request)
    {
        var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
        var perPage = int.TryParse(query["per_page"], out var parsed) ? Math.Clamp(parsed, 1, 100) : 30;
        lock (_gate)
        {
            if (LiveCount() == 0)
            {
                return Conflict();
            }

            var tip = Resolve(query["sha"] ?? DefaultBranch);
            if (tip is null)
            {
                return NotFound();
            }

            return Ok(Ancestors(tip).OrderByDescending(node => node.Order).Take(perPage).Select(node => CommitJson(node, withFiles: false)));
        }
    }

    private HttpResponseMessage ServeCommit(HttpRequestMessage request, string prefix)
    {
        var reference = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath)[prefix.Length..];
        lock (_gate)
        {
            return Resolve(reference) is { } node ? Ok(CommitJson(node, withFiles: true)) : NotFound();
        }
    }

    private HttpResponseMessage ServeCompare(HttpRequestMessage request, string prefix)
    {
        var spec = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath)[prefix.Length..];
        var split = spec.IndexOf("...", StringComparison.Ordinal);
        if (split < 0)
        {
            return NotFound();
        }

        lock (_gate)
        {
            if (LiveCount() == 0)
            {
                return Conflict();
            }

            if (Resolve(spec[..split]) is not { } baseNode || Resolve(spec[(split + 3)..]) is not { } headNode)
            {
                return NotFound();
            }

            var baseAncestors = Ancestors(baseNode).ToHashSet();
            var headAncestors = Ancestors(headNode).ToHashSet();
            var ahead = headAncestors.Where(node => !baseAncestors.Contains(node)).OrderBy(node => node.Order).ToList();
            var behind = baseAncestors.Count(node => !headAncestors.Contains(node));
            var status = (ahead.Count, behind) switch
            {
                (0, 0) => "identical",
                (_, 0) => "ahead",
                (0, _) => "behind",
                _ => "diverged",
            };

            var files = new Dictionary<string, GitHubFileChange>(StringComparer.Ordinal);
            foreach (var change in ahead.SelectMany(node => node.Files))
            {
                files[change.Path] = change;
            }

            return Ok(new
            {
                status,
                ahead_by = ahead.Count,
                behind_by = behind,
                base_commit = new { sha = baseNode.Sha },
                merge_base_commit = new { sha = MergeBase(baseAncestors, headAncestors)?.Sha ?? baseNode.Sha },
                commits = ahead.Select(node => CommitJson(node, withFiles: false)),
                files = files.Values.Select(FileJson),
            });
        }
    }

    private HttpResponseMessage ServeContents(HttpRequestMessage request, string prefix)
    {
        var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath)[prefix.Length..];
        string? content;
        lock (_gate)
        {
            content = _files.GetValueOrDefault(path);
        }

        return content is null
            ? NotFound()
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content))
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/vnd.github.raw+json") },
                },
            };
    }

    // ---- graph (call under _gate) -------------------------------------------------------------------------------

    private int LiveCount() => _commits.Values.Count(node => !node.Forgotten);

    /// <summary>A branch name or a commit SHA; null when unknown or forgotten.</summary>
    private Node? Resolve(string reference)
    {
        var sha = _branches.GetValueOrDefault(reference, reference);
        return _commits.TryGetValue(sha, out var node) && !node.Forgotten ? node : null;
    }

    /// <summary>The commit and every reachable ancestor; forgotten parents end that line of history.</summary>
    private HashSet<Node> Ancestors(Node tip)
    {
        var seen = new HashSet<Node>();
        var pending = new Stack<Node>();
        pending.Push(tip);
        while (pending.TryPop(out var node))
        {
            if (!seen.Add(node))
            {
                continue;
            }

            foreach (var parent in node.Parents)
            {
                if (_commits.TryGetValue(parent, out var parentNode) && !parentNode.Forgotten)
                {
                    pending.Push(parentNode);
                }
            }
        }

        return seen;
    }

    private Node? MergeBase(HashSet<Node> left, HashSet<Node> right)
    {
        var common = left.Where(right.Contains).ToList();
        var shadowed = common.SelectMany(node => Ancestors(node).Where(ancestor => ancestor != node)).ToHashSet();
        return common.Where(node => !shadowed.Contains(node)).OrderByDescending(node => node.Order).FirstOrDefault();
    }

    // ---- JSON ---------------------------------------------------------------------------------------------------

    private static object CommitJson(Node node, bool withFiles)
    {
        var person = new { name = "Test Author", email = "author@example.com", date = Epoch.AddMinutes(node.Order) };
        return new
        {
            sha = node.Sha,
            commit = new { author = person, committer = person, message = node.Message },
            parents = node.Parents.Select(parent => new { sha = parent }),
            files = withFiles ? node.Files.Select(FileJson) : null,
        };
    }

    private static object FileJson(GitHubFileChange change) => new
    {
        filename = change.Path,
        previous_filename = change.PreviousPath,
        status = change.Status,
        additions = change.Additions,
        deletions = change.Deletions,
        patch = change.Patch,
    };

    private static HttpResponseMessage Ok(object body) => Respond(HttpStatusCode.OK, body);

    private static HttpResponseMessage NotFound() => Respond(HttpStatusCode.NotFound, new { message = "Not Found" });

    private static HttpResponseMessage Conflict() => Respond(HttpStatusCode.Conflict, new { message = "Git Repository is empty." });

    private static HttpResponseMessage Respond(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
    };

    /// <summary>Answers 304 to a matching If-None-Match, else the body with the ETag.</summary>
    private static HttpResponseMessage WithEtag(HttpRequestMessage request, string etag, Func<HttpResponseMessage> body)
    {
        var tag = new EntityTagHeaderValue(etag);
        var response = request.Headers.IfNoneMatch.Any(candidate => candidate.Tag == tag.Tag)
            ? new HttpResponseMessage(HttpStatusCode.NotModified)
            : body();
        response.Headers.ETag = tag;
        return response;
    }

    private sealed class Node(string sha, string message, List<string> parents, GitHubFileChange[] files, int order)
    {
        public string Sha { get; } = sha;

        public string Message { get; } = message;

        public List<string> Parents { get; } = parents;

        public GitHubFileChange[] Files { get; } = files;

        public int Order { get; } = order;

        public bool Forgotten { get; set; }
    }
}
