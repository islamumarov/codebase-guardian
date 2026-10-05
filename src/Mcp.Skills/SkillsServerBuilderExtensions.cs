using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Mcp.Skills;

public static class SkillsServerBuilderExtensions
{
    /// <summary>
    /// Serves skills over MCP (SEP-2640): <c>skills/list</c>, <c>skills/get</c>, <c>resources/directory/read</c>, and
    /// skill files through <c>resources/list</c> and <c>resources/read</c>, alongside any other resources.
    /// The catalog is loaded when the MCP server options are first built, so invalid skills fail server startup.
    /// </summary>
    public static IMcpServerBuilder WithSkills(this IMcpServerBuilder builder, Action<SkillsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.Configure(configure);
        builder.Services.TryAddSingleton(sp => SkillCatalog.Load(sp.GetRequiredService<IOptions<SkillsOptions>>().Value));
        builder.Services.AddSingleton<IConfigureOptions<McpServerOptions>, SkillsConfigureOptions>();
        return builder;
    }
}
