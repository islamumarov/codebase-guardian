using System.Text;

namespace CodebaseGuardian.Git;

/// <summary>The patch byte cap shared by local and remote diffs, so both modes cut a patch the same way.</summary>
internal static class PatchCap
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>
    /// The patch unchanged when its UTF-8 form fits <paramref name="maxBytes"/>; otherwise its first <paramref name="maxBytes"/>
    /// bytes, backed off so no multi-byte character is split, and Truncated = true.
    /// </summary>
    public static (string Patch, bool Truncated) Apply(string patch, int maxBytes)
    {
        var bytes = Utf8.GetBytes(patch);
        if (bytes.Length <= maxBytes)
        {
            return (patch, false);
        }

        var cut = maxBytes;
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80)
        {
            cut--; // do not split a multi-byte character
        }

        return (Utf8.GetString(bytes, 0, cut), true);
    }
}
