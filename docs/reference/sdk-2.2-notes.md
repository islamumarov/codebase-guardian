# MCP C# SDK 2.2.0 — Implementer Notes (verified)

Facts below were verified against the SDK v2.2.0 source and by running probe programs against the 2.2.0 packages (stdio-equivalent stream transport on protocol 2026-07-28 and 2025-11-25, and Streamable HTTP in `Stateless` and `StatefulForInitializeClients` modes). Treat them as ground truth; when the SDK behaves differently from the MCP spec, the note says so.

## 1. Experimental diagnostics

Projects that use the APIs below need:

```xml
<NoWarn>$(NoWarn);MCPEXP001;MCPEXP002;MCPEXP004</NoWarn>
```

`McpServerRequestHandler.RoutingNameParameter`, `McpServerOptions.RequestHandlers`, MRTR types and the Tasks extension are `[Experimental]`. Add only the IDs the compiler actually reports.

## 2. Custom JSON-RPC methods

```csharp
public sealed class McpServerRequestHandler
{
    public required string Method { get; init; }
    public string? RoutingNameParameter { get; init; }   // [Experimental]
    public required Func<JsonRpcRequest, CancellationToken, ValueTask<JsonNode?>> Handler { get; init; }
}
// McpServerOptions.RequestHandlers : IList<McpServerRequestHandler>? — custom handlers take precedence over built-ins for the same method.
```

Register them the way the official Tasks extension does — an `IConfigureOptions<McpServerOptions>` singleton added by a builder extension, so the handler class can take DI dependencies:

```csharp
public static IMcpServerBuilder WithSomething(this IMcpServerBuilder builder)
{
    builder.Services.AddSingleton<IConfigureOptions<McpServerOptions>, SomethingConfigureOptions>();
    return builder;
}

internal sealed class SomethingConfigureOptions(MyCatalog catalog) : IConfigureOptions<McpServerOptions>
{
    public void Configure(McpServerOptions options)
    {
        options.Capabilities ??= new ServerCapabilities();
        options.Capabilities.Extensions ??= new Dictionary<string, object>();
        options.Capabilities.Extensions["io.modelcontextprotocol/skills"] = new JsonObject();
        options.RequestHandlers ??= new List<McpServerRequestHandler>();
        options.RequestHandlers.Add(new McpServerRequestHandler { Method = "skills/list", Handler = HandleListAsync });
    }
}
```

Rules learned the hard way:

| Fact | Consequence |
|---|---|
| The SDK does **not** add `resultType` to custom-handler results. It does add `_meta["io.modelcontextprotocol/serverInfo"]`. | Every custom result object must include `"resultType": "complete"`. |
| The SDK does **not** add `ttlMs`/`cacheScope` to custom results (it defaults them only for built-in cacheable results such as `resources/read`). | Add `ttlMs` (integer ms) and `cacheScope` (`"public"`/`"private"`) yourself where the spec requires them. |
| Returning `null` from a handler sends **no response**: stdio clients hang until timeout, HTTP clients get "POST response completed without a reply". | Never return `null`; return at least `{"resultType":"complete"}`. |
| `throw new McpProtocolException(message, (McpErrorCode)(-32011))` → client sees that code and message; entries added to `ex.Data` are sent as `error.data`. | Use this for protocol errors with custom codes and `data`. |
| Any other exception → `-32603` with message "An error occurred." (details hidden). | Map expected failures to `McpProtocolException` explicitly. |
| Unknown method → `-32601` (HTTP status 404). | — |
| Request params: `request.Params` (`JsonNode?`, may be null). Per-request metadata: `request.Context?.ProtocolVersion`, `request.Context?.ClientCapabilities` (its `Extensions` values are `JsonElement`), `request.Context?.Items` (`IDictionary<string, object?>`). | On 2026-07-28 use `request.Context` for client capabilities. |
| `RoutingNameParameter` set ⇒ Streamable HTTP **requires** an `Mcp-Name` header for that method; SDK clients only send it when the request carries `Context = new JsonRpcMessageContext { RoutingName = ... }`. | Do **not** set `RoutingNameParameter` for `skills/*` or `events/*` (the spec defines `Mcp-Name` only for `tools/call`, `resources/read`, `prompts/get`). |

## 3. Getting the `McpServer` inside a custom handler (needed to send notifications)

There is no `Server` property on `JsonRpcRequest`. The verified pattern is an incoming message filter that stashes the request-bound server into the per-message `Items` bag:

```csharp
builder.WithMessageFilters(f => f.AddIncomingFilter(next => async (ctx, ct) =>
{
    if (ctx.JsonRpcMessage is JsonRpcRequest) ctx.Items[ServerItemKey] = ctx.Server;
    await next(ctx, ct);
}));

// inside the handler:
var server = (McpServer)request.Context!.Items![ServerItemKey]!;
await server.SendMessageAsync(new JsonRpcNotification { Method = "notifications/events/event", Params = payload }, ct);
```

`ctx.Server` is a request-bound (`DestinationBoundMcpServer`) instance: notifications sent through it are routed to the same channel as the request (stdout on stdio, the request's SSE response stream on HTTP — including **stateless** HTTP). Verified: a handler that sends notifications and then keeps the request open delivers them to the client while the request is in flight, in all transports.

## 4. Cancellation of long-lived requests

| Transport | Verified behaviour |
|---|---|
| Streamable HTTP (stateless or stateful) | Client cancellation closes the stream; the handler's `CancellationToken` fires. |
| stdio / stream transport | **SDK 2.2.0 client quirk:** cancelling the token passed to `McpClient.SendRequestAsync` does **not** reach the server — the handler's token never fires. Sending the notification explicitly **does** work: `await client.SendNotificationAsync(NotificationMethods.CancelledNotification, new CancelledNotificationParams { RequestId = request.Id });` → handler token fires, client task ends `Canceled`. |

Tests and the demo client that cancel a long-lived request over stdio must send `notifications/cancelled` explicitly. To know the id, create the request with an explicit id: `new JsonRpcRequest { Id = new RequestId("stream-1"), Method = "events/stream", Params = ... }` (the SDK keeps a caller-supplied id).

## 5. Capabilities

- `ServerCapabilities.Extensions` is `IDictionary<string, object>?` → serialized under `capabilities.extensions`. Use `new JsonObject()` for `{}`.
- `ServerCapabilities` has **no** extension-data bag: you cannot emit arbitrary top-level capability keys. `ServerCapabilities.Experimental` (`IDictionary<string, object>?`) serializes as `capabilities.experimental`.
- Clients read them with `client.ServerCapabilities.Extensions` (values arrive as `JsonElement`).
- `McpServerOptions.ServerInstructions` (string) → `instructions` in `server/discover` / `initialize`.

## 6. Built-in primitives

- Tools: `[McpServerToolType]` class + `[McpServerTool(Name = "repo_status", ReadOnly = true, ...)]` methods, `[Description]` on method/params. Parameters can be DI services, `McpServer`, `RequestContext<CallToolRequestParams>`, `IProgress<ProgressNotificationValue>`, `CancellationToken`. Register with `.WithTools<T>()`. Annotation properties on the attribute: `Destructive`, `Idempotent`, `OpenWorld`, `ReadOnly`, `Title`, `UseStructuredContent`.
- Resources: `[McpServerResourceType]` class + `[McpServerResource(UriTemplate = "guardian://checks/{runId}/log", Name = "...", MimeType = "...")]`. A URI template with parameters is listed by `resources/templates/list`; without parameters, by `resources/list`. Register with `.WithResources<T>()`. `McpServerResource.Create(...)` builds one from a delegate.
- Cacheable built-in results (`ListToolsResult`, `ReadResourceResult`, `ListResourcesResult`, `DiscoverResult`, …) expose `TimeSpan? TimeToLive` and `CacheScope? CacheScope`; on 2026-07-28 requests the SDK sets `TimeToLive ??= TimeSpan.Zero`.
- Resource-not-found on 2026-07-28 is `-32602` (`McpErrorCode.InvalidParams`).

## 7. Elicitation / MRTR (multi round-trip requests)

- `server.ElicitAsync(...)` works over stdio (2026-07-28 and 2025-11-25) but throws `InvalidOperationException("Elicitation is not supported in stateless mode.")` over stateless HTTP; there `server.ClientCapabilities` is `null`.
- The stateless-safe way is MRTR from a tool: when `server.IsMrtrSupported` is true, throw
  `new InputRequiredException(inputRequests: new Dictionary<string, InputRequest> { ["confirm"] = InputRequest.ForElicitation(new ElicitRequestParams { Message = "...", RequestedSchema = new() }) }, requestState: "<opaque>")`.
  The client answers and retries; on retry `context.Params.RequestState` is set and `context.Params.InputResponses["confirm"]` holds the answer (deserialize with `InputResponse.ElicitResultJsonTypeInfo`, check `Action == "accept"`). Verified end to end with an SDK client that has an `ElicitationHandler`.
- If `IsMrtrSupported` is false, throwing `InputRequiredException` becomes `-32603`. Fall back to a tool argument (e.g. `confirm: true`).

## 8. Tasks extension (`ModelContextProtocol.Extensions.Tasks`)

- Enable: `.WithTasks(new InMemoryMcpTaskStore { DefaultPollIntervalMs = 250 }, o => o.ExecutionModeSelector = ctx => ...)`. Advertises `capabilities.extensions["io.modelcontextprotocol/tasks"]`, adds `tasks/get`, `tasks/update`, `tasks/cancel`.
- `McpTasksOptions.ExecutionModeSelector : Func<RequestContext<CallToolRequestParams>, McpTaskExecutionMode>`; default returns `Optional` for **every** tool. Values: `Synchronous`, `Optional`, `Required`. A tool runs as a task only on 2026-07-28 requests whose client capabilities include the tasks extension; `Required` without opt-in fails the call.
- Client: `client.CallToolAsTaskAsync(new CallToolRequestParams { Name = ... })` → `IsTask`, `TaskCreated` (`TaskId`, `Status`, `PollIntervalMs`), then `client.GetTaskAsync(taskId)` → `WorkingTaskResult` / `CompletedTaskResult` (`.Result` JSON → `CallToolResult`) / `FailedTaskResult` / `CancelledTaskResult` / `InputRequiredTaskResult`. Convenience: `client.CallToolWithPollingAsync(...)`. The client must declare `Extensions = { ["io.modelcontextprotocol/tasks"] = new JsonObject() }` in `McpClientOptions.Capabilities` to get task behaviour.
- See `samples/TasksExtension/Program.cs` in the SDK repo (tag v2.2.0) for the canonical flow.

## 9. Transports and hosting

- stdio: `.WithStdioServerTransport()`; logs must go to stderr (`builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)`).
- HTTP: `.WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)` and `app.MapMcp("/mcp")`. `HttpServerSessionMode` values seen: `Stateless`, `StatefulForInitializeClients` (hybrid: stateful only for legacy `initialize` clients). Both served 2026-07-28 SDK clients correctly in the probes.
- Default SDK client negotiates **2026-07-28** (uses `server/discover` and per-request `_meta`); `McpClientOptions.ProtocolVersion = "2025-11-25"` forces the legacy handshake.

## 10. In-process testing recipes

Stream (stdio-equivalent) pair:

```csharp
Pipe c2s = new(), s2c = new();
var services = new ServiceCollection();
services.AddLogging();
services.AddMcpServer(o => o.ServerInfo = new() { Name = "test", Version = "1.0" })
        .WithStreamServerTransport(c2s.Reader.AsStream(), s2c.Writer.AsStream());
// ... add the same registrations the app uses
await using var sp = services.BuildServiceProvider();
var server = sp.GetRequiredService<McpServer>();
_ = server.RunAsync(cts.Token);
await using var client = await McpClient.CreateAsync(
    new StreamClientTransport(c2s.Writer.AsStream(), s2c.Reader.AsStream()),
    new McpClientOptions { Capabilities = new ClientCapabilities { Extensions = new Dictionary<string, object> { ["io.modelcontextprotocol/skills"] = new JsonObject() } } });
```

Raw custom requests and notifications from the client:

```csharp
JsonRpcResponse resp = await client.SendRequestAsync(new JsonRpcRequest { Method = "skills/list", Params = new JsonObject() }, ct);
JsonNode? result = resp.Result;
await using var reg = client.RegisterNotificationHandler("notifications/events/event", (n, ct) => { /* n.Params */ return default; });
```

Custom-code errors surface on the client as `McpProtocolException` with `ErrorCode` = the code and `Data` = the error data.

HTTP: host a real Kestrel on `http://127.0.0.1:0` (`WebApplication.CreateSlimBuilder()`, `builder.WebHost.UseUrls("http://127.0.0.1:0")`, `app.MapMcp("/mcp")`, `await app.StartAsync()`, read `app.Urls`), then connect with `new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(url + "/mcp"), TransportMode = HttpTransportMode.StreamableHttp })`.

## 11. Reference sources

- SDK source: https://github.com/modelcontextprotocol/csharp-sdk/tree/v2.2.0 (`src/ModelContextProtocol.Extensions.Tasks/Server/McpTasksBuilderExtensions.cs` is the model for registering an extension).
- Wire formats for Skills and Events: `docs/reference/skills-events-wire-format.md`.
