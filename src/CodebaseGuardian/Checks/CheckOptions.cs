namespace CodebaseGuardian.Checks;

/// <summary>Settings of the check runner, bound from <c>Guardian:Checks</c>. Only configuration can set the command, never a tool argument.</summary>
public sealed class CheckOptions
{
    public const string SectionName = "Guardian:Checks";

    /// <summary>The executable to run; <c>null</c> means detect one from the repository.</summary>
    public string? Command { get; set; }

    public string[] Arguments { get; set; } = [];

    public int TimeoutMinutes { get; set; } = 15;

    /// <summary>How many finished runs (with their logs) are kept in memory.</summary>
    public int MaxRuns { get; set; } = 50;
}
