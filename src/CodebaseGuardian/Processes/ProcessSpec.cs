namespace CodebaseGuardian.Processes;

/// <summary>An external process to run: executable, argument list (never a shell command line), and working directory.</summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Per stream; excess output is dropped and <see cref="ProcessResult.OutputTruncated"/> is set.</summary>
    public int MaxOutputBytes { get; init; } = 1_048_576;

    /// <summary>Variables added to (or, for the same name, overriding) the inherited environment.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}
