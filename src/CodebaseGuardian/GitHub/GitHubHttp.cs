using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodebaseGuardian.GitHub;

/// <summary>One logical GitHub REST conversation over a given HttpClient. A null token sends no Authorization header.</summary>
internal sealed partial class GitHubHttp(HttpClient http, GitHubOptions options, TimeProvider time)
{
    public const int MaxPages = 5;
    private const int MaxErrorMessageLength = 500;

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>The API base URL with a trailing slash; set on the HttpClient when it has none.</summary>
    public Uri BaseAddress
    {
        get
        {
            http.BaseAddress ??= new Uri(options.ApiBaseUrl.TrimEnd('/') + "/");
            return http.BaseAddress;
        }
    }

    /// <summary>Non-success statuses not in <paramref name="allow"/> throw (mapping below). The caller disposes the response.</summary>
    public Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string pathOrUrl, object? body, string? token, CancellationToken ct,
        string? accept = null, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        params HttpStatusCode[] allow) =>
        SendCoreAsync(method, pathOrUrl, body, token, ct, accept, completion, ifNoneMatch: null, allow);

    public async Task<T> SendJsonAsync<T>(HttpMethod method, string path, object? body, string? token, CancellationToken ct)
    {
        using var response = await SendAsync(method, path, body, token, ct);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
            ?? throw new GitHubApiException((int)response.StatusCode, "GitHub returned an empty response.");
    }

    /// <summary>Follows <c>Link: rel="next"</c> for at most <paramref name="maxPages"/> pages, never off the API host. Statuses in allow end the listing with what was read so far.</summary>
    public async Task<List<T>> ListAsync<T>(
        string firstPath, string? token, CancellationToken ct, int maxPages = MaxPages, params HttpStatusCode[] allow)
    {
        var results = new List<T>();
        string? next = firstPath;
        for (var page = 0; page < maxPages && next is not null; page++)
        {
            using var response = await SendAsync(HttpMethod.Get, next, null, token, ct, allow: allow);
            if (!response.IsSuccessStatusCode)
            {
                break;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            if (content.TrimStart().StartsWith('['))
            {
                results.AddRange(JsonSerializer.Deserialize<List<T>>(content, Json) ?? []);
            }
            else if (JsonSerializer.Deserialize<T>(content, Json) is { } single)
            {
                results.Add(single);
            }

            next = NextLink(response);
        }

        return results;
    }

    /// <summary>GET with If-None-Match from <paramref name="cache"/>; 304 returns the cached entry with NotModified = true; 200 refreshes the cache.</summary>
    public async Task<ConditionalResponse> GetConditionalAsync(
        string path, string? token, GitHubConditionalCache cache, CancellationToken ct, params HttpStatusCode[] allow)
    {
        var cached = cache.TryGet(path, out var entry);
        using var response = await SendCoreAsync(
            HttpMethod.Get, path, null, token, ct, accept: null, HttpCompletionOption.ResponseContentRead,
            cached ? entry.ETag : null, [.. allow, HttpStatusCode.NotModified]);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return cached
                ? new ConditionalResponse(HttpStatusCode.OK, entry.Body, entry.NextLink, NotModified: true)
                : throw new GitHubApiException(304, "GitHub returned 304 without a cached response.");
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            return new ConditionalResponse(response.StatusCode, body, null, NotModified: false);
        }

        var next = NextLink(response);
        if (TryHeader(response, "etag", out var etag))
        {
            cache.Set(path, etag, body, next);
        }

        return new ConditionalResponse(response.StatusCode, body, next, NotModified: false);
    }

    /// <summary>The validated next-page URL of a response, or null.</summary>
    public string? NextLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        var baseAddress = BaseAddress;
        foreach (var match in LinkPattern().Matches(string.Join(',', values)).Cast<Match>())
        {
            if (match.Groups["rel"].Value == "next"
                && Uri.TryCreate(baseAddress, match.Groups["url"].Value, out var uri)
                && uri.Scheme == baseAddress.Scheme
                && uri.Host == baseAddress.Host
                && uri.Port == baseAddress.Port)
            {
                return uri.AbsoluteUri;
            }
        }

        return null;
    }

    [GeneratedRegex("<(?<url>[^>]+)>\\s*;\\s*rel=\"(?<rel>[^\"]+)\"")]
    private static partial Regex LinkPattern();

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpMethod method, string pathOrUrl, object? body, string? token, CancellationToken ct,
        string? accept, HttpCompletionOption completion, string? ifNoneMatch, HttpStatusCode[] allow)
    {
        _ = BaseAddress; // make sure relative paths resolve
        using var request = new HttpRequestMessage(method, pathOrUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept ?? "application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("codebase-guardian");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        var response = await http.SendAsync(request, completion, ct);
        if (response.IsSuccessStatusCode || allow.Contains(response.StatusCode))
        {
            return response;
        }

        using (response)
        {
            throw await ToExceptionAsync(response, token, ct);
        }
    }

    private async Task<Exception> ToExceptionAsync(HttpResponseMessage response, string? token, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (status == 401)
        {
            return new GitHubApiException(401, "GitHub rejected the token (401).");
        }

        if (status is 403 or 429)
        {
            if (TryHeader(response, "retry-after", out var retryAfter)
                && double.TryParse(retryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                return new GitHubRateLimitException(time.GetUtcNow().AddSeconds(seconds));
            }

            if (TryHeader(response, "x-ratelimit-remaining", out var remaining) && remaining == "0"
                && TryHeader(response, "x-ratelimit-reset", out var reset)
                && long.TryParse(reset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
            {
                return new GitHubRateLimitException(DateTimeOffset.FromUnixTimeSeconds(epoch));
            }
        }

        var message = Describe(await response.Content.ReadAsStringAsync(ct), response.ReasonPhrase);
        if (!string.IsNullOrEmpty(token))
        {
            message = message.Replace(token, "***", StringComparison.Ordinal);
        }

        return new GitHubApiException(status, message.Length > MaxErrorMessageLength ? message[..MaxErrorMessageLength] : message);
    }

    private static bool TryHeader(HttpResponseMessage response, string name, out string value)
    {
        value = response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() ?? string.Empty : string.Empty;
        return value.Length > 0;
    }

    private static string Describe(string body, string? fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var parts = new List<string>();
            if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                parts.Add(message.GetString()!);
            }

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                foreach (var error in errors.EnumerateArray())
                {
                    var text = error.ValueKind switch
                    {
                        JsonValueKind.String => error.GetString(),
                        JsonValueKind.Object when error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String => m.GetString(),
                        _ => null,
                    };
                    if (!string.IsNullOrEmpty(text))
                    {
                        parts.Add(text);
                    }
                }
            }

            if (parts.Count > 0)
            {
                return string.Join("; ", parts);
            }
        }
        catch (JsonException)
        {
            // Not a JSON error body; fall through to the status text.
        }

        return string.IsNullOrWhiteSpace(fallback) ? "GitHub request failed." : fallback;
    }
}

internal sealed record ConditionalResponse(HttpStatusCode Status, string Body, string? NextLink, bool NotModified);
