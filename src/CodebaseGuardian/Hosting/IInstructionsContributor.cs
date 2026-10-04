namespace CodebaseGuardian.Hosting;

/// <summary>
/// Contributes one section of the server <c>instructions</c> that clients receive when they connect.
/// Register implementations as <see cref="IInstructionsContributor"/> services; they are composed when the
/// MCP server options are built, so registration order relative to <c>AddCodebaseGuardian</c> does not matter.
/// </summary>
public interface IInstructionsContributor
{
    /// <summary>Sections are emitted in ascending order; contributors with equal order keep registration order.</summary>
    int Order { get; }

    /// <summary>The section text, or <c>null</c>/empty to contribute nothing.</summary>
    string? GetSection();
}
