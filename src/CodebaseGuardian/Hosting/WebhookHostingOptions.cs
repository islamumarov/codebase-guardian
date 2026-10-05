namespace CodebaseGuardian.Hosting;

public sealed class WebhookHostingOptions
{
    public const string SectionName = "Guardian:Webhooks";

    /// <summary>Webhook delivery is offered only when this is set, the transport is HTTP and at least one API key exists.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Development flag (spec section 7); copied into <c>EventsOptions.Webhooks</c>.</summary>
    public bool AllowInsecureLoopback { get; set; }
}
