using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mcp.Skills;

internal sealed class SkillsConfigureOptions(SkillCatalog catalog, IOptions<SkillsOptions> skillsOptions)
    : IConfigureOptions<McpServerOptions>
{
    private readonly SkillsOptions _options = skillsOptions.Value;
    private string UriPrefix => _options.UriScheme + "://";

    public void Configure(McpServerOptions options)
    {
        // A resources capability makes the SDK install default list/read handlers, which the filters below wrap.
        options.Capabilities ??= new ServerCapabilities();
        options.Capabilities.Resources ??= new ResourcesCapability();
        options.Capabilities.Extensions ??= new Dictionary<string, object>();
        options.Capabilities.Extensions[SkillsProtocol.ExtensionId] = new JsonObject { ["directoryRead"] = true };

        options.RequestHandlers ??= [];
        options.RequestHandlers.Add(new McpServerRequestHandler { Method = SkillsProtocol.ListMethod, Handler = HandleList });
        options.RequestHandlers.Add(new McpServerRequestHandler { Method = SkillsProtocol.GetMethod, Handler = HandleGet });
        options.RequestHandlers.Add(new McpServerRequestHandler { Method = SkillsProtocol.DirectoryReadMethod, Handler = HandleDirectoryRead });

        options.Filters.Request.ListResourcesFilters.Add(next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            foreach (var skill in catalog.Skills)
            {
                var skillMd = catalog.FindFile(skill.Uri);
                result.Resources.Add(new Resource
                {
                    Uri = skill.Uri,
                    Name = skill.Name,
                    Description = skill.Description,
                    MimeType = SkillsProtocol.MarkdownMimeType,
                    Size = skillMd?.Size,
                });
            }

            return result;
        });

        options.Filters.Request.ReadResourceFilters.Add(next => (context, cancellationToken) =>
        {
            var uri = context.Params?.Uri;
            return uri is not null && uri.StartsWith(UriPrefix, StringComparison.Ordinal)
                ? ValueTask.FromResult(ReadSkillResource(uri))
                : next(context, cancellationToken);
        });
    }

    private ReadResourceResult ReadSkillResource(string uri)
    {
        var file = catalog.FindFile(uri);
        if (file is null)
        {
            throw new McpProtocolException($"Resource not found: {uri}", McpErrorCode.InvalidParams)
            {
                Data = { ["uri"] = uri },
            };
        }

        ResourceContents contents = file.IsUtf8Text
            ? new TextResourceContents { Uri = uri, MimeType = file.MimeType, Text = new UTF8Encoding(false).GetString(file.Content) }
            : BlobResourceContents.FromBytes(file.Content, uri, file.MimeType);

        return new ReadResourceResult
        {
            Contents = [contents],
            TimeToLive = _options.CacheTtl,
            CacheScope = CacheScope.Public,
        };
    }

    private ValueTask<JsonNode?> HandleList(JsonRpcRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult<JsonNode?>(SkillJson.ListResult(catalog, _options.CacheTtl));

    private ValueTask<JsonNode?> HandleGet(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        var uri = RequireUri(request);
        var skill = catalog.FindSkill(uri)
            ?? throw new McpProtocolException($"No skill is served at {uri}", McpErrorCode.InvalidParams);
        return ValueTask.FromResult<JsonNode?>(SkillJson.GetResult(skill, _options.CacheTtl));
    }

    private ValueTask<JsonNode?> HandleDirectoryRead(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        var uri = RequireUri(request);
        if (!catalog.TryListDirectory(uri, out var children))
        {
            throw new McpProtocolException($"{uri} is not a directory resource", McpErrorCode.InvalidParams);
        }

        return ValueTask.FromResult<JsonNode?>(SkillJson.DirectoryResult(children));
    }

    private static string RequireUri(JsonRpcRequest request) =>
        request.Params is JsonObject p
        && p.TryGetPropertyValue("uri", out var node)
        && node is JsonValue value
        && value.TryGetValue<string>(out var uri)
            ? uri
            : throw new McpProtocolException("params.uri must be a string", McpErrorCode.InvalidParams);
}
