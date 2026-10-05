namespace CodebaseGuardian.Git;

public sealed class GitException(string message, int exitCode, string standardError) : Exception(message)
{
    public int ExitCode { get; } = exitCode;

    public string StandardError { get; } = standardError;
}
