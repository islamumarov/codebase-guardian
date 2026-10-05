namespace CodebaseGuardian.Processes;

public interface IProcessRunner
{
    /// <summary>
    /// Runs the process to completion. Never throws for a non-zero exit code.
    /// </summary>
    /// <exception cref="ExecutableNotFoundException">The executable could not be started.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; the process tree is killed first.</exception>
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default);
}
