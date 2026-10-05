namespace CodebaseGuardian.Security;

public static class Redactor
{
    /// <summary>Values of 12 or more characters keep their first and last four; anything shorter is fully masked.</summary>
    public static string Redact(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return secret.Length >= 12 ? string.Concat(secret.AsSpan(0, 4), "****", secret.AsSpan(secret.Length - 4)) : "****";
    }
}
