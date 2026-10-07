using System.Net;
using System.Text;
using CodebaseGuardian.GitHub;
using CodebaseGuardian.Hosting;
using CodebaseGuardian.Sources;
using CodebaseGuardian.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodebaseGuardian.Tests.App;

public class GitHubTarballTests
{
    private const string ApiPath = "/repos/acme/widgets/tarball/main";
    private const string CodeloadPath = "/acme/widgets/legacy.tar.gz/abc1234";
    private const string CodeloadUrl = "https://codeload.github.com/acme/widgets/legacy.tar.gz/abc1234?token=fake";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IGitHubRepositoryApi Create(FakeGitHubApi api, string? apiBaseUrl = null)
    {
        var settings = new Dictionary<string, string?>();
        if (apiBaseUrl is not null)
        {
            settings["Guardian:GitHub:ApiBaseUrl"] = apiBaseUrl;
        }

        var services = new ServiceCollection();
        services.AddGitHubIntegration(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        api.Install(services);
        return services.BuildServiceProvider().GetRequiredService<IGitHubRepositoryApi>();
    }

    private static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.TryAddWithoutValidation("Location", location);
        return response;
    }

    private static FakeGitHubApi RedirectingTo(string location, HttpStatusCode status = HttpStatusCode.Found) =>
        new FakeGitHubApi()
            .Map(HttpMethod.Get, ApiPath, (_, _) => Redirect(location, status))
            .Map(HttpMethod.Get, CodeloadPath, (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("tarball") });

    private static async Task<string> ReadAll(Stream stream)
    {
        await using (stream)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(Ct);
        }
    }

    [Fact]
    public async Task The_token_goes_to_the_API_host_and_never_to_codeload()
    {
        var api = RedirectingTo(CodeloadUrl);

        var body = await ReadAll(await Create(api).OpenTarballAsync("main", Ct));

        Assert.Equal("tarball", body);
        Assert.Equal(2, api.Requests.Count);
        Assert.Equal("api.github.com", api.RequestUris[0].Host);
        Assert.Equal($"Bearer {FakeGitHubApi.Token}", api.Requests[0].Headers["Authorization"]);
        Assert.Equal("application/vnd.github+json", api.Requests[0].Headers["Accept"]);
        Assert.Equal("2022-11-28", api.Requests[0].Headers["X-GitHub-Api-Version"]);
        Assert.Equal("codebase-guardian", api.Requests[0].Headers["User-Agent"]);
        Assert.Equal("codeload.github.com", api.RequestUris[1].Host);
        Assert.False(api.Requests[1].Headers.ContainsKey("Authorization"));
        Assert.Equal("codebase-guardian", api.Requests[1].Headers["User-Agent"]);
        Assert.DoesNotContain(FakeGitHubApi.Token, api.RequestUris[1].ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task Every_redirect_status_is_followed_without_the_token(HttpStatusCode status)
    {
        var api = RedirectingTo(CodeloadUrl, status);

        Assert.Equal("tarball", await ReadAll(await Create(api).OpenTarballAsync("main", Ct)));
        Assert.False(api.Requests[1].Headers.ContainsKey("Authorization"));
    }

    [Theory]
    [InlineData("https://evil.example/x.tgz?token=secret123")]
    [InlineData("http://codeload.github.com/acme/widgets/legacy.tar.gz/abc1234?token=secret123")]
    [InlineData("https://codeload.github.com.evil.example/acme?token=secret123")]
    [InlineData("https://codeload.github.com:8443/acme/widgets/legacy.tar.gz/abc1234?token=secret123")]
    [InlineData("https://user@evil.example/acme?token=secret123")]
    [InlineData("//codeload.github.com/acme?token=secret123")]
    [InlineData("/acme/widgets/legacy.tar.gz/abc1234?token=secret123")]
    public async Task A_redirect_to_an_unexpected_target_is_refused_without_following_it_or_naming_it(string location)
    {
        var api = RedirectingTo(location);

        var error = await Assert.ThrowsAsync<GitHubApiException>(() => Create(api).OpenTarballAsync("main", Ct));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal("Refusing tarball redirect to an unexpected host.", error.Message);
        Assert.DoesNotContain("evil.example", error.Message);
        Assert.DoesNotContain("token=", error.Message);
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task A_redirect_without_a_Location_is_refused()
    {
        var api = new FakeGitHubApi().Map(HttpMethod.Get, ApiPath, (_, _) => new HttpResponseMessage(HttpStatusCode.Found));

        var error = await Assert.ThrowsAsync<GitHubApiException>(() => Create(api).OpenTarballAsync("main", Ct));

        Assert.Equal(502, error.StatusCode);
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task A_second_redirect_is_refused_without_naming_the_target()
    {
        var api = new FakeGitHubApi()
            .Map(HttpMethod.Get, ApiPath, (_, _) => Redirect(CodeloadUrl))
            .Map(HttpMethod.Get, CodeloadPath, (_, _) => Redirect("https://codeload.github.com/other?token=secret123"));

        var error = await Assert.ThrowsAsync<GitHubApiException>(() => Create(api).OpenTarballAsync("main", Ct));

        Assert.Equal(502, error.StatusCode);
        Assert.DoesNotContain("token=", error.Message);
        Assert.DoesNotContain("codeload", error.Message);
        Assert.Equal(2, api.Requests.Count);
    }

    [Fact]
    public async Task A_GitHub_Enterprise_redirect_may_stay_on_the_API_host_but_not_go_to_codeload()
    {
        var allowed = new FakeGitHubApi()
            .Map(HttpMethod.Get, "/api/v3/repos/acme/widgets/tarball/main", (_, _) => Redirect("https://ghe.acme.test/acme/widgets/legacy.tar.gz/abc1234"))
            .Map(HttpMethod.Get, CodeloadPath, (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ghes") });

        Assert.Equal("ghes", await ReadAll(await Create(allowed, "https://ghe.acme.test/api/v3").OpenTarballAsync("main", Ct)));
        Assert.False(allowed.Requests[1].Headers.ContainsKey("Authorization"));

        var refused = new FakeGitHubApi()
            .Map(HttpMethod.Get, "/api/v3/repos/acme/widgets/tarball/main", (_, _) => Redirect(CodeloadUrl));

        var error = await Assert.ThrowsAsync<GitHubApiException>(
            () => Create(refused, "https://ghe.acme.test/api/v3").OpenTarballAsync("main", Ct));
        Assert.Equal(502, error.StatusCode);
    }

    [Fact]
    public async Task A_direct_200_from_the_API_host_is_accepted()
    {
        var api = new FakeGitHubApi().Map(HttpMethod.Get, ApiPath, (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("direct") });

        Assert.Equal("direct", await ReadAll(await Create(api).OpenTarballAsync("main", Ct)));
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task An_unknown_revision_is_a_revision_not_found_error_on_either_leg()
    {
        var onApi = new FakeGitHubApi();
        var onCodeload = new FakeGitHubApi().Map(HttpMethod.Get, ApiPath, (_, _) => Redirect(CodeloadUrl));

        var first = await Assert.ThrowsAsync<GitHubRevisionNotFoundException>(() => Create(onApi).OpenTarballAsync("main", Ct));
        var second = await Assert.ThrowsAsync<GitHubRevisionNotFoundException>(() => Create(onCodeload).OpenTarballAsync("main", Ct));

        Assert.Equal("main", first.Revision);
        Assert.Equal("main", second.Revision);
    }

    [Fact]
    public async Task Other_codeload_failures_are_errors_that_do_not_contain_the_URL()
    {
        var api = new FakeGitHubApi()
            .Map(HttpMethod.Get, ApiPath, (_, _) => Redirect(CodeloadUrl))
            .Map(HttpMethod.Get, CodeloadPath, (_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var error = await Assert.ThrowsAsync<GitHubApiException>(() => Create(api).OpenTarballAsync("main", Ct));

        Assert.Equal(500, error.StatusCode);
        Assert.DoesNotContain("token=", error.Message);
        Assert.DoesNotContain("codeload", error.Message);
    }

    [Fact]
    public async Task A_ref_is_escaped_per_segment_and_traversal_refs_are_rejected()
    {
        var api = new FakeGitHubApi();
        var github = Create(api);

        await Assert.ThrowsAsync<GitHubRevisionNotFoundException>(() => github.OpenTarballAsync("feat/a b", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => github.OpenTarballAsync("a/../b", Ct));

        Assert.Equal("/repos/acme/widgets/tarball/feat/a%20b", Assert.Single(api.RequestUris).AbsolutePath);
    }

    [Fact]
    public async Task Disposing_the_stream_disposes_the_response()
    {
        var content = new RecordingStream(Encoding.UTF8.GetBytes("tarball"));
        var api = new FakeGitHubApi().Map(HttpMethod.Get, ApiPath, (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(content) });

        var stream = await Create(api).OpenTarballAsync("main", Ct);
        Assert.False(content.Disposed);
        Assert.Equal((byte)'t', stream.ReadByte());

        await stream.DisposeAsync();

        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task Cancellation_during_the_download_is_not_turned_into_another_error()
    {
        using var cts = new CancellationTokenSource();
        var api = new FakeGitHubApi()
            .Map(HttpMethod.Get, ApiPath, (_, _) => Redirect(CodeloadUrl))
            .Map(HttpMethod.Get, CodeloadPath, (_, _) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(api).OpenTarballAsync("main", cts.Token));
    }

    [Fact]
    public async Task The_fake_repository_serves_its_files_as_a_tarball_through_the_redirect()
    {
        var api = new FakeGitHubApi();
        var repository = new FakeGitHubRepository(api)
            .Commit("abc1234def", "first", [])
            .SetBranch("main", "abc1234def")
            .File("src/a.cs", "class A;")
            .File("README.md", "hi");

        await using var tarball = await Create(api).OpenTarballAsync("main", Ct);
        var files = new List<SnapshotFile>();
        await foreach (var file in SnapshotTarReader.ReadAsync(
            tarball, new SnapshotCaps(1000, 10_000, 100),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, Ct))
        {
            files.Add(file);
        }

        Assert.Equal(["README.md", "src/a.cs"], files.Select(f => f.Path).Order());
        Assert.Equal("codeload.github.com", api.RequestUris[1].Host);
        Assert.False(api.Requests[1].Headers.ContainsKey("Authorization"));
        Assert.NotNull(repository);
    }

    private sealed class RecordingStream(byte[] data) : MemoryStream(data)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
