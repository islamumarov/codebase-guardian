using System.Net;

namespace Mcp.Events;

/// <summary>Webhook subscription and delivery settings. Members marked "Task 21" are read by the delivery loop only.</summary>
public sealed class WebhookOptions
{
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromHours(1);
    /// <summary>Shortest grant; shorter suggestions are raised to it (ruling R9; tests may lower it).</summary>
    public TimeSpan MinTtl { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>Longest grant; longer or absent-expiry suggestions are lowered to it (ruling R9).</summary>
    public TimeSpan MaxTtl { get; set; } = TimeSpan.FromHours(24);
    public int MaxSubscriptionsPerPrincipal { get; set; } = 100;
    /// <summary>Development flag: http:// callback URLs and loopback/private targets are allowed.</summary>
    public bool AllowInsecureLoopback { get; set; }
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan SecretRotationGrace { get; set; } = TimeSpan.FromMinutes(5);
    public IList<TimeSpan> RetryDelays { get; } = new List<TimeSpan> { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2) }; // Task 21
    public TimeSpan RetryWindow { get; set; } = TimeSpan.FromMinutes(15);   // Task 21
    public double SuspendFailureRate { get; set; } = 0.95;                  // Task 21
    public int SuspendMinAttempts { get; set; } = 100;                      // Task 21
    public TimeSpan SuspendWindow { get; set; } = TimeSpan.FromMinutes(60); // Task 21
    /// <summary>Test seam; <see langword="null"/> resolves with <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>.</summary>
    public Func<string, CancellationToken, ValueTask<IPAddress[]>>? ResolveHost { get; set; }
}
