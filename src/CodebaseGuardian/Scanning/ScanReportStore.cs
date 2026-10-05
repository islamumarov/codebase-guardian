namespace CodebaseGuardian.Scanning;

/// <summary>The last <see cref="Capacity"/> scan reports, in memory.</summary>
public sealed class ScanReportStore
{
    public const int Capacity = 20;

    private readonly object _gate = new();
    private readonly Queue<ScanReport> _reports = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _reports.Count;
            }
        }
    }

    public void Add(ScanReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            _reports.Enqueue(report);
            while (_reports.Count > Capacity)
            {
                _reports.Dequeue();
            }
        }
    }

    public ScanReport? Get(string scanId)
    {
        lock (_gate)
        {
            return _reports.FirstOrDefault(r => r.ScanId == scanId);
        }
    }
}
