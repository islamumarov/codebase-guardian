using Microsoft.Extensions.Options;

namespace CodebaseGuardian.Hosting;

/// <summary>
/// The base section of the server instructions (Order 0), plus the rules for composing all sections.
/// </summary>
internal sealed class GuardianInstructions(IOptions<GuardianOptions> options) : IInstructionsContributor
{
    public int Order => 0;

    public string? GetSection() =>
        $"Codebase Guardian is watching the Git repository at {options.Value.RepositoryPath}. " +
        "It offers tools to inspect the repository, run its checks and audit it for secrets and vulnerable dependencies, " +
        "resources and events that report what changes, and skills that explain how to react.";

    /// <summary>
    /// Joins the contributors' sections in <see cref="IInstructionsContributor.Order"/> with a blank line between them,
    /// skipping contributors that return nothing. Returns <c>null</c> when no contributor has anything to say.
    /// </summary>
    public static string? Compose(IEnumerable<IInstructionsContributor> contributors)
    {
        ArgumentNullException.ThrowIfNull(contributors);

        var sections = contributors
            .OrderBy(contributor => contributor.Order) // stable: equal orders keep registration order
            .Select(contributor => contributor.GetSection()?.Trim())
            .Where(section => !string.IsNullOrEmpty(section))
            .ToList();

        return sections.Count == 0 ? null : string.Join("\n\n", sections);
    }
}
