using System.Collections.Frozen;

namespace CodebaseGuardian.Hosting;

/// <summary>
/// Maps the Guardian command-line switches onto <c>Guardian:*</c> configuration keys.
/// </summary>
/// <remarks>
/// Feed <see cref="Normalize"/> output and <see cref="SwitchMappings"/> to the configuration command-line provider.
/// The provider treats every switch as <c>--key value</c>, so a bare flag such as <c>--auto-checks</c> would swallow
/// the next argument; <see cref="Normalize"/> turns bare flags into <c>--flag=value</c> first.
/// </remarks>
public static class GuardianCommandLine
{
    private const string AutoChecksSwitch = "--auto-checks";
    private const string NoWatchSwitch = "--no-watch";

    public static IReadOnlyDictionary<string, string> SwitchMappings { get; } = new Dictionary<string, string>
    {
        ["--repo"] = Key(nameof(GuardianOptions.RepositoryPath)),
        ["--transport"] = Key(nameof(GuardianOptions.Transport)),
        ["--urls"] = Key(nameof(GuardianOptions.HttpUrl)),
        [AutoChecksSwitch] = Key(nameof(GuardianOptions.AutoChecks)),
        ["--skills-dir"] = Key(nameof(GuardianOptions.SkillsDirectory)),
        [NoWatchSwitch] = Key(nameof(GuardianOptions.WatchEnabled)),
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Rewrites the boolean flags (<c>--auto-checks</c>, <c>--no-watch</c>) to the single-token form
    /// <c>--flag=true|false</c>, expressed in terms of the option they set (<c>--no-watch</c> sets
    /// <c>WatchEnabled</c> to <c>false</c>). Every other argument is passed through unchanged, in order.
    /// </summary>
    public static string[] Normalize(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var normalized = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var separator = arg.IndexOf('=');
            var name = separator < 0 ? arg : arg[..separator];

            if (!TryGetFlag(name, out var inverted))
            {
                normalized.Add(arg);
                continue;
            }

            // The value is "--flag=value", a following "true"/"false", or absent (a bare flag means true).
            var text = separator >= 0
                ? arg[(separator + 1)..]
                : i + 1 < args.Length && bool.TryParse(args[i + 1], out _) ? args[++i] : "true";

            if (!bool.TryParse(text, out var value))
            {
                normalized.Add(arg); // not a boolean: leave it for configuration binding to report
                continue;
            }

            normalized.Add($"{name}={(value != inverted ? "true" : "false")}");
        }

        return [.. normalized];
    }

    private static bool TryGetFlag(string name, out bool inverted)
    {
        inverted = name.Equals(NoWatchSwitch, StringComparison.OrdinalIgnoreCase);
        return inverted || name.Equals(AutoChecksSwitch, StringComparison.OrdinalIgnoreCase);
    }

    private static string Key(string property) => $"{GuardianOptions.SectionName}:{property}";
}
