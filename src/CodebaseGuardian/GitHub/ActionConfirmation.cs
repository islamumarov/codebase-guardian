using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodebaseGuardian.GitHub;

public enum ConfirmationStatus { Confirmed, Declined, ConfirmArgumentRequired }

public interface IActionConfirmation
{
    /// <summary>
    /// Asks the user (MRTR elicitation) when the client can show a prompt; the <paramref name="confirmArgument"/> is ignored
    /// on that path. Otherwise falls back to the explicit <c>confirm: true</c> argument.
    /// </summary>
    ConfirmationStatus Confirm(McpServer server, RequestContext<CallToolRequestParams> context, string summary, bool confirmArgument);
}

public sealed class ActionConfirmation : IActionConfirmation
{
    private const string InputKey = "confirm";

    // One key per process: tokens cannot be forged or replayed from another server instance.
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    public ConfirmationStatus Confirm(McpServer server, RequestContext<CallToolRequestParams> context, string summary, bool confirmArgument)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(summary);

        var elicitation = context.JsonRpcRequest.Context?.ClientCapabilities?.Elicitation ?? server.ClientCapabilities?.Elicitation;
        if (!server.IsMrtrSupported || elicitation is null)
        {
            return confirmArgument ? ConfirmationStatus.Confirmed : ConfirmationStatus.ConfirmArgumentRequired;
        }

        var token = ConfirmationToken(context.Params?.Name ?? string.Empty, context.Params?.Arguments);
        var state = context.Params?.RequestState;
        if (state is null)
        {
            throw new InputRequiredException(
                new Dictionary<string, InputRequest>
                {
                    [InputKey] = InputRequest.ForElicitation(new ElicitRequestParams { Message = summary, RequestedSchema = new() }),
                },
                token);
        }

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(token)))
        {
            throw new McpException("Confirmation does not match this request; call the tool again.");
        }

        if (context.Params?.InputResponses is not { } responses || !responses.TryGetValue(InputKey, out var response))
        {
            throw new McpException("Confirmation does not match this request; call the tool again.");
        }

        var result = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo);
        return result?.Action == "accept" ? ConfirmationStatus.Confirmed : ConfirmationStatus.Declined;
    }

    /// <summary>base64url(HMAC-SHA256(key, toolName + "\n" + arguments without "confirm", keys sorted ordinally)).</summary>
    internal static string ConfirmationToken(string toolName, IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in (arguments ?? []).Where(p => p.Key != InputKey).OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(name);
                writer.WriteRawValue(value.GetRawText());
            }

            writer.WriteEndObject();
        }

        var payload = Encoding.UTF8.GetBytes(toolName + "\n").Concat(buffer.ToArray()).ToArray();
        return Base64Url.EncodeToString(HMACSHA256.HashData(Key, payload));
    }
}
