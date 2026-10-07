using System.Net;

namespace CodebaseGuardian.GitHub;

public sealed partial class GitHubRepositoryApi
{
    /// <summary>The named client for tarballs: it must not follow redirects itself, because the redirect target gets no token.</summary>
    public const string ArchiveHttpClientName = "github-archive";

    private const string CodeloadHost = "codeload.github.com";
    private const string GitHubApiHost = "api.github.com";

    private static readonly HttpStatusCode[] Redirects =
        [HttpStatusCode.MovedPermanently, HttpStatusCode.Found, HttpStatusCode.TemporaryRedirect, HttpStatusCode.PermanentRedirect];

    /// <summary>
    /// The gzip-compressed tar of <paramref name="ref"/>, streamed (nothing is buffered or written to disk). The API request carries
    /// the token; its redirect is followed once, only to the codeload host (the API host itself on GHES), without any credentials.
    /// Disposing the returned stream disposes the HTTP response.
    /// </summary>
    public async Task<Stream> OpenTarballAsync(string @ref, CancellationToken ct)
    {
        var escapedRef = EscapeRef(@ref, "ref");
        var session = await PrepareAsync(ct, ArchiveHttpClientName);

        var response = await session.Http.SendAsync(
            HttpMethod.Get, session.Path("tarball", escapedRef), null, session.Token, ct,
            completion: HttpCompletionOption.ResponseHeadersRead,
            allow: [.. Redirects, HttpStatusCode.NotFound]);
        try
        {
            if (Redirects.Contains(response.StatusCode))
            {
                var target = response.Headers.Location;
                if (!IsAllowedRedirect(session.Http.BaseAddress, target))
                {
                    // The message must not carry the URL: it holds a short-lived credential.
                    throw new GitHubApiException(502, "Refusing tarball redirect to an unexpected host.");
                }

                response.Dispose();
                response = await session.Http.GetWithoutCredentialsAsync(target!, ct);
                if (Redirects.Contains(response.StatusCode))
                {
                    throw new GitHubApiException(502, "Refusing a second tarball redirect.");
                }
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new GitHubRevisionNotFoundException(@ref, session.Repository);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new GitHubApiException((int)response.StatusCode, $"GitHub could not serve the tarball ({(int)response.StatusCode}).");
            }

            var content = await response.Content.ReadAsStreamAsync(ct);
            var stream = new ResponseOwningStream(content, response);
            response = null!; // owned by the stream now
            return stream;
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>Absolute https, no user info, on the codeload host (github.com) or the API host and port (GHES).</summary>
    private static bool IsAllowedRedirect(Uri apiBase, Uri? location)
    {
        if (location is not { IsAbsoluteUri: true } || location.Scheme != Uri.UriSchemeHttps || location.UserInfo.Length > 0)
        {
            return false;
        }

        var (host, port) = apiBase.Host == GitHubApiHost ? (CodeloadHost, 443) : (apiBase.Host, apiBase.Port);
        return location.Host == host && location.Port == port;
    }

    /// <summary>A read-only view of the response body that disposes the response with it.</summary>
    private sealed class ResponseOwningStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override int ReadByte() => inner.ReadByte();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            response.Dispose();
            await base.DisposeAsync();
        }
    }
}
