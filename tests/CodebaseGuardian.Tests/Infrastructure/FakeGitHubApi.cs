using CodebaseGuardian.GitHub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodebaseGuardian.Tests.Infrastructure;

/// <summary>An in-memory GitHub REST API: canned responses routed by method and path, so no test touches the network.</summary>
public sealed class FakeGitHubApi : HttpMessageHandler
{
    public const string Token = "test-token-not-a-secret";

    private readonly object _gate = new();
    private readonly List<(HttpMethod Method, string Path, Func<HttpRequestMessage, string, HttpResponseMessage> Respond)> _routes = [];
    private readonly List<(HttpMethod Method, string PathAndQuery, string Body, IReadOnlyDictionary<string, string> Headers)> _requests = [];
    private readonly List<string> _unmatched = [];

    public IReadOnlyList<(HttpMethod Method, string PathAndQuery, string Body, IReadOnlyDictionary<string, string> Headers)> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>"METHOD path" of every request no route matched; those get a 404.</summary>
    public IReadOnlyList<string> Unmatched
    {
        get
        {
            lock (_gate)
            {
                return [.. _unmatched];
            }
        }
    }

    /// <summary>Routes by method and path; the query is ignored for matching. The first matching route wins.</summary>
    public FakeGitHubApi Map(HttpMethod method, string path, Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        lock (_gate)
        {
            _routes.Add((method, path, respond));
        }

        return this;
    }

    public FakeGitHubApi MapJson(HttpMethod method, string path, int status, string json, IDictionary<string, string>? headers = null) =>
        Map(method, path, (_, _) =>
        {
            var response = new HttpResponseMessage((System.Net.HttpStatusCode)status)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
            if (headers is not null)
            {
                foreach (var (name, value) in headers)
                {
                    response.Headers.TryAddWithoutValidation(name, value);
                }
            }

            return response;
        });

    /// <summary>
    /// Replaces the token provider with one that returns <see cref="Token"/>, pins the repository to acme/widgets, and
    /// makes this handler the primary handler of the "github" named client.
    /// </summary>
    public void Install(IServiceCollection services)
    {
        services.RemoveAll<IGitHubTokenProvider>();
        services.AddSingleton<IGitHubTokenProvider>(new FixedTokenProvider());
        services.RemoveAll<IGitHubRepositoryResolver>();
        services.AddSingleton<IGitHubRepositoryResolver>(new FixedResolver());
        services.AddHttpClient(GitHubClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => this);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        var uri = request.RequestUri!;
        // AbsolutePath is escaped; compare unescaped so routes can be written with the plain branch name.
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        Func<HttpRequestMessage, string, HttpResponseMessage>? respond = null;
        lock (_gate)
        {
            _requests.Add((request.Method, uri.PathAndQuery, body, headers));
            foreach (var route in _routes)
            {
                if (route.Method == request.Method && string.Equals(route.Path, path, StringComparison.Ordinal))
                {
                    respond = route.Respond;
                    break;
                }
            }

            if (respond is null)
            {
                _unmatched.Add($"{request.Method} {path}");
            }
        }

        var response = respond?.Invoke(request, body)
            ?? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"message\":\"Not Found\"}", System.Text.Encoding.UTF8, "application/json"),
            };
        response.RequestMessage = request;
        return response;
    }

    private sealed class FixedTokenProvider : IGitHubTokenProvider
    {
        public ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(Token);
    }

    private sealed class FixedResolver : IGitHubRepositoryResolver
    {
        public ValueTask<GitHubRepositoryRef?> ResolveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<GitHubRepositoryRef?>(new GitHubRepositoryRef("acme", "widgets"));
    }
}
