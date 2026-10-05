namespace CodebaseGuardian.Tests.Infrastructure;

/// <summary>
/// Secret-shaped test values, assembled at runtime so that no source file contains a literal that push protection
/// or a scanner would flag. Shared by every test that needs a secret.
/// </summary>
public static class FakeSecrets
{
    public static string AwsAccessKeyId() => "AKIA" + "IOSFODNN7EXAMPLE";
    public static string GitHubToken() => "ghp_" + new string('a', 36);
    public static string GitHubFineGrainedPat() => "github_" + "pat_" + new string('b', 82);
    public static string SlackToken() => "xoxb-" + "1234567890-abcdefghij";
    public static string StripeLiveKey() => "sk_" + "live_" + "4eC39HqLyjWDarjtT1zdp7dc";
    public static string PrivateKeyHeader() => "-----BEGIN " + "RSA PRIVATE" + " KEY-----";
    public static string Jwt() => "eyJ" + "hbGciOiJIUzI1NiJ9" + "." + "eyJ" + "zdWIiOiIxMjM0NTY3ODkw" + "." + "SflKxwRJSMeKKF2QT4fwpM";
    public static string GenericAssignment() => "api_key = " + "\"" + "Zx9fQ2mK8vLp4TnR7sW1" + "\"";
    public static string LowEntropyAssignment() => "password = " + "\"" + new string('a', 20) + "\"";
}
