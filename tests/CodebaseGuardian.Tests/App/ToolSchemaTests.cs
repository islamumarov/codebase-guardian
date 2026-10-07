using System.Text.Json;
using CodebaseGuardian.Tests.Infrastructure;

namespace CodebaseGuardian.Tests.App;

public class ToolSchemaTests
{
    private static readonly string[] DescribingKeywords = ["type", "$ref", "anyOf", "oneOf", "allOf", "enum", "const"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_value_in_every_output_schema_is_described()
    {
        using var repo = TempGitRepo.Create();
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var undescribed = new List<string>();
        foreach (var tool in await server.Client.ListToolsAsync(cancellationToken: Ct))
        {
            if (tool.ProtocolTool.OutputSchema is { } schema)
            {
                CollectUndescribed(schema, tool.Name, undescribed);
            }
        }

        Assert.Empty(undescribed);
    }

    [Theory]
    [InlineData("poll_events", "events", "timestamp")]
    [InlineData("recent_commits", "commits", "committedAt")]
    public async Task Timestamps_are_described_as_date_time_strings(string tool, string list, string property)
    {
        using var repo = TempGitRepo.Create();
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);
        var schema = Assert.Single(await server.Client.ListToolsAsync(cancellationToken: Ct), t => t.Name == tool).ProtocolTool.OutputSchema!.Value;

        var timestamp = schema.GetProperty("properties").GetProperty(list).GetProperty("items").GetProperty("properties").GetProperty(property);

        Assert.Equal(JsonValueKind.Object, timestamp.ValueKind);
        Assert.Equal("string", timestamp.GetProperty("type").GetString());
        Assert.Equal("date-time", timestamp.GetProperty("format").GetString());
    }

    // A value schema of `true` or `{}` accepts anything: clients learn nothing from it, and some reject the tool.
    private static void CollectUndescribed(JsonElement schema, string path, List<string> undescribed)
    {
        if (schema.ValueKind != JsonValueKind.Object || !DescribingKeywords.Any(keyword => schema.TryGetProperty(keyword, out _)))
        {
            undescribed.Add($"{path}: {schema.GetRawText()}");
            return;
        }

        if (schema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                CollectUndescribed(property.Value, $"{path}.{property.Name}", undescribed);
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            CollectUndescribed(items, $"{path}[]", undescribed);
        }

        foreach (var combinator in new[] { "anyOf", "oneOf", "allOf" })
        {
            if (schema.TryGetProperty(combinator, out var branches))
            {
                var index = 0;
                foreach (var branch in branches.EnumerateArray())
                {
                    CollectUndescribed(branch, $"{path}.{combinator}[{index++}]", undescribed);
                }
            }
        }
    }
}
