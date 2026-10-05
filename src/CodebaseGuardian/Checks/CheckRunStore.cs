using Microsoft.Extensions.Options;

namespace CodebaseGuardian.Checks;

/// <summary>The newest <see cref="CheckOptions.MaxRuns"/> runs, in memory.</summary>
public sealed class CheckRunStore(IOptions<CheckOptions> options)
{
    private readonly object _gate = new();
    private readonly Queue<CheckRun> _runs = new();

    public CheckRun? Latest
    {
        get
        {
            lock (_gate)
            {
                return _runs.LastOrDefault();
            }
        }
    }

    public void Add(CheckRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_gate)
        {
            _runs.Enqueue(run);
            while (_runs.Count > Math.Max(1, options.Value.MaxRuns))
            {
                _runs.Dequeue();
            }
        }
    }

    public CheckRun? Get(string runId)
    {
        lock (_gate)
        {
            return _runs.FirstOrDefault(r => r.RunId == runId);
        }
    }
}
