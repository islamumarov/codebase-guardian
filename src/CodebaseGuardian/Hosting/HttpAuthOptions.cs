using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.Hosting;

/// <summary>Optional bearer authentication for the HTTP transport. With no keys configured there is no authentication.</summary>
public sealed class HttpAuthOptions
{
    public const string SectionName = "Guardian:Http";

    /// <summary>Principal name to API key.</summary>
    public Dictionary<string, string> ApiKeys { get; } = new(StringComparer.Ordinal);
}

internal sealed partial class HttpAuthOptionsValidator : IValidateOptions<HttpAuthOptions>
{
    internal const int MinimumKeyLength = 32;

    public ValidateOptionsResult Validate(string? name, HttpAuthOptions options)
    {
        var failures = new List<string>();
        foreach (var (principal, key) in options.ApiKeys)
        {
            if (!PrincipalPattern().IsMatch(principal))
            {
                failures.Add($"Guardian:Http:ApiKeys principal '{principal}' must match ^[A-Za-z0-9._@-]{{1,64}}$.");
            }

            if ((key ?? "").Length < MinimumKeyLength)
            {
                failures.Add($"Guardian:Http:ApiKeys key for principal '{principal}' must be at least {MinimumKeyLength} characters.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    [GeneratedRegex(@"^[A-Za-z0-9._@-]{1,64}\z")]
    private static partial Regex PrincipalPattern();
}
