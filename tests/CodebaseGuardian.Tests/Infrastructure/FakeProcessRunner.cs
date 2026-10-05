using CodebaseGuardian.Processes;

namespace CodebaseGuardian.Tests.Infrastructure;

/// <summary>
/// An <see cref="IProcessRunner"/> with canned results. The first registered match wins; a call nothing matches goes to
/// <c>fallback</c> (for example the real runner, so git keeps working in a hosted application) or fails the test.
/// </summary>
public sealed class FakeProcessRunner(IProcessRunner? fallback = null) : IProcessRunner
{
    private readonly object _gate = new();
    private readonly List<(Func<ProcessSpec, bool> Match, Func<CancellationToken, Task<ProcessResult>> Respond)> _handlers = [];
    private readonly List<ProcessSpec> _calls = [];

    public IReadOnlyList<ProcessSpec> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public FakeProcessRunner On(Func<ProcessSpec, bool> match, ProcessResult result) =>
        Add(match, _ => Task.FromResult(result));

    /// <summary>The call waits until <paramref name="completion"/> is completed (or the call is cancelled).</summary>
    public FakeProcessRunner OnBlocking(Func<ProcessSpec, bool> match, TaskCompletionSource<ProcessResult> completion) =>
        Add(match, ct => completion.Task.WaitAsync(ct));

    public static ProcessResult Result(int exitCode, string standardOutput = "", string standardError = "", bool timedOut = false) =>
        new(exitCode, standardOutput, standardError, timedOut, false, TimeSpan.FromMilliseconds(5));

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
    {
        Func<CancellationToken, Task<ProcessResult>>? respond = null;
        lock (_gate)
        {
            if (!IsGitCallForFallback(spec))
            {
                _calls.Add(spec);
            }

            foreach (var handler in _handlers)
            {
                if (handler.Match(spec))
                {
                    respond = handler.Respond;
                    break;
                }
            }
        }

        if (respond is not null)
        {
            return respond(cancellationToken);
        }

        return fallback?.RunAsync(spec, cancellationToken)
            ?? throw new InvalidOperationException($"No fake result registered for {spec.FileName} {string.Join(' ', spec.Arguments)}.");
    }

    // Calls handled by the fallback (git) are not interesting to tests that assert on the commands they registered.
    private bool IsGitCallForFallback(ProcessSpec spec) => fallback is not null && spec.FileName == "git";

    private FakeProcessRunner Add(Func<ProcessSpec, bool> match, Func<CancellationToken, Task<ProcessResult>> respond)
    {
        lock (_gate)
        {
            _handlers.Add((match, respond));
        }

        return this;
    }
}
