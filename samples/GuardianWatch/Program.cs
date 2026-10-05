using System.Text;
using System.Text.Json.Nodes;
using GuardianWatch;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// GuardianWatch: starts Codebase Guardian over stdio, streams four events and, for each event that names a suggested
// skill, loads that skill and verifies it against its published digest and size.
//
//   dotnet run --project samples/GuardianWatch -- --repo <path> [--server <CodebaseGuardian.dll | project dir>]

const string SubscriptionIdKey = "io.modelcontextprotocol/subscriptionId";
string[] watched = ["repo.commit.created", "repo.dependencies.changed", "checks.failed", "security.secret_detected"];

string? repo = null, server = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--repo" && i + 1 < args.Length) repo = args[++i];
    else if (args[i] == "--server" && i + 1 < args.Length) server = args[++i];
    else
    {
        Console.Error.WriteLine($"Unknown or incomplete argument '{args[i]}'.");
        repo = null;
        break;
    }
}

if (repo is null)
{
    Console.Error.WriteLine("Usage: GuardianWatch --repo <path> [--server <path to CodebaseGuardian.dll or project directory>]");
    return 2;
}

string[] serverArguments = server switch
{
    null => ["run", "--project", FindServerProject() ?? "src/CodebaseGuardian", "--"],
    _ when server.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) => [server],
    _ => ["run", "--project", server, "--"],
};

await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "codebase-guardian",
    Command = "dotnet",
    Arguments = [.. serverArguments, "--repo", Path.GetFullPath(repo)],
}));

Console.WriteLine($"Server: {client.ServerInfo.Name} {client.ServerInfo.Version}");
Console.WriteLine("Instructions:");
Console.WriteLine(Indent(client.ServerInstructions ?? "(none)"));

var skills = await RequestAsync("skills/list", new JsonObject());
Console.WriteLine("Skills: " + string.Join(", ", skills["skills"]!.AsArray().Select(s => (string?)s!["frontmatter"]!["name"])));
var events = await RequestAsync("events/list", new JsonObject());
Console.WriteLine("Events: " + string.Join(", ", events["events"]!.AsArray().Select(e => (string?)e!["name"])));

// SDK 2.2.0 over stdio: cancelling the token of SendRequestAsync never reaches the server, so every stream gets an
// explicit id and is stopped with notifications/cancelled.
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

var console = new SemaphoreSlim(1); // notification handlers run concurrently; keep each event's lines together
await using var handler = client.RegisterNotificationHandler("notifications/events/event", async (n, ct) =>
{
    var p = (JsonObject)n.Params!;
    var data = p["data"] as JsonObject ?? new JsonObject();
    var id = p["_meta"]?[SubscriptionIdKey]?.ToString();
    await console.WaitAsync(ct);
    try
    {
        Console.WriteLine($"[{id}] {p["name"]} {p["timestamp"]} {Compact(data)}");
        if (data["suggestedSkill"] is JsonValue uri)
        {
            try
            {
                await LoadSkillAsync((string)uri!, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Console.WriteLine($"  skill {SkillName((string)uri!)} NOT verified: {e.Message}");
            }
        }
    }
    finally
    {
        console.Release();
    }
});

var streams = watched.Select(name =>
{
    var id = $"watch-{name}";
    var request = new JsonRpcRequest { Id = new RequestId(id), Method = "events/stream", Params = new JsonObject { ["name"] = name } };
    return (Id: id, Response: client.SendRequestAsync(request, CancellationToken.None));
}).ToList();
Console.WriteLine($"Watching {string.Join(", ", watched)}. Press Ctrl+C to stop.");

var exitCode = 0;
try
{
    var ended = await Task.WhenAny(Task.Delay(Timeout.Infinite, shutdown.Token), Task.WhenAny(streams.Select(s => (Task)s.Response)));
    if (ended is not { IsCanceled: true })
    {
        // A stream only ends before Ctrl+C when the server closed it or the connection failed.
        var failed = streams.First(s => s.Response.IsCompleted);
        Console.Error.WriteLine($"Stream {failed.Id} ended: {(failed.Response.IsFaulted ? failed.Response.Exception!.GetBaseException().Message : "closed by the server")}");
        exitCode = 1;
    }
}
catch (OperationCanceledException)
{
}

foreach (var (id, _) in streams)
{
    try
    {
        await client.SendNotificationAsync("notifications/cancelled", new CancelledNotificationParams { RequestId = new RequestId(id) });
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"Could not cancel {id}: {e.Message}");
    }
}

Console.WriteLine("Stopped.");
return exitCode;

async Task<JsonObject> RequestAsync(string method, JsonObject parameters)
{
    var response = await client.SendRequestAsync(new JsonRpcRequest { Method = method, Params = parameters });
    return (JsonObject)response.Result!;
}

async ValueTask LoadSkillAsync(string uri, CancellationToken ct)
{
    var got = await client.SendRequestAsync(new JsonRpcRequest { Method = "skills/get", Params = new JsonObject { ["uri"] = uri } }, ct);
    var skill = (JsonObject)((JsonObject)got.Result!)["skill"]!;
    var entry = SkillVerifier.FindEntry(skill, uri);
    if (entry is null)
    {
        Console.WriteLine($"  skill {uri} NOT verified: its manifest does not list SKILL.md");
        return;
    }

    var read = await client.ReadResourceAsync(uri, cancellationToken: ct);
    var content = read.Contents[0] switch
    {
        TextResourceContents text => Encoding.UTF8.GetBytes(text.Text),
        BlobResourceContents blob => blob.DecodedData.ToArray(),
        _ => [],
    };

    var problem = SkillVerifier.Verify(entry, content);
    if (problem is not null)
    {
        Console.WriteLine($"  skill {uri} NOT verified: {problem}");
        return;
    }

    Console.WriteLine($"  skill {skill["frontmatter"]!["name"]} verified ({content.Length} bytes)");
    foreach (var line in Encoding.UTF8.GetString(content).Split('\n').Take(5))
    {
        Console.WriteLine($"    | {line.TrimEnd('\r')}");
    }
}

static string Indent(string text) => string.Join('\n', text.Split('\n').Select(l => "  " + l.TrimEnd('\r')));

static string Compact(JsonObject data)
{
    var text = data.ToJsonString();
    return text.Length <= 200 ? text : text[..197] + "...";
}

// Without --server: the server project of the checkout this sample was built from.
static string? FindServerProject()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "src", "CodebaseGuardian", "CodebaseGuardian.csproj");
        if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
    }

    return null;
}

// skill://security-audit/SKILL.md -> security-audit
static string SkillName(string uri) => uri.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? uri;
