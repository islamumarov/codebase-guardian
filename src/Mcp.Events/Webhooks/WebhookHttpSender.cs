using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace Mcp.Events;

/// <summary>
/// Sends webhooks through a handler that resolves and checks the target address on every connection (so a DNS answer that
/// changes between subscribe and delivery is still caught) and connects to the validated address while the request keeps
/// the original host for <c>Host</c> and SNI. Redirects are never followed.
/// </summary>
public sealed class WebhookHttpSender : IWebhookSender, IDisposable
{
    private const int MaxBodyBytes = 4096;

    private readonly WebhookOptions _options;
    private readonly TimeProvider _time;
    private readonly HttpClient _client;

    public WebhookHttpSender(WebhookOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = options.RequestTimeout,
            ConnectCallback = ConnectAsync,
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = options.RequestTimeout };
    }

    public async Task<WebhookSendResult> SendAsync(Uri url, string messageId, ReadOnlyMemory<byte> body, string subscriptionId,
        IReadOnlyList<byte[]> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        var timestamp = _time.GetUtcNow().ToUnixTimeSeconds();
        var content = new ByteArrayContent(body.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content = content;
        request.Headers.TryAddWithoutValidation("webhook-id", messageId);
        request.Headers.TryAddWithoutValidation("webhook-timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("webhook-signature", WebhookSigner.Sign(messageId, timestamp, body.Span, keys));
        request.Headers.TryAddWithoutValidation("X-MCP-Subscription-Id", subscriptionId);

        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300)
            {
                return new WebhookSendResult(true, status, null, await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false));
            }

            return new WebhookSendResult(false, status, status >= 500 ? "http_5xx" : "http_4xx", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or TimeoutException)
        {
            return new WebhookSendResult(false, null, Categorize(ex), null);
        }
    }

    public void Dispose() => _client.Dispose();

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaxBodyBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static string Categorize(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case BlockedAddressException:
                    return "connection_refused";
                case AuthenticationException:
                    return "tls_error";
                case TimeoutException or OperationCanceledException:
                    return "timeout";
                case HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }:
                    return "tls_error";
                case SocketException { SocketErrorCode: SocketError.TimedOut }:
                    return "timeout";
            }
        }

        // Refused, unreachable, unknown host and anything else that stopped the connection from being made.
        return "connection_refused";
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var resolve = _options.ResolveHost
            ?? ((name, ct) => new ValueTask<IPAddress[]>(Dns.GetHostAddressesAsync(name, ct)));
        var addresses = await resolve(host, cancellationToken).ConfigureAwait(false);
        var allowed = _options.AllowInsecureLoopback ? addresses : addresses.Where(a => !WebhookAddressPolicy.IsBlocked(a)).ToArray();
        if (allowed.Length == 0)
        {
            throw new BlockedAddressException();
        }

        Exception? last = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw last!;
    }

    private sealed class BlockedAddressException() : IOException("The callback target resolves to a blocked address.");
}
