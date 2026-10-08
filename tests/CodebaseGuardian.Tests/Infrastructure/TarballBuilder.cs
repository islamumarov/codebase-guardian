using System.Formats.Tar;
using System.IO.Compression;

namespace CodebaseGuardian.Tests.Infrastructure;

/// <summary>Hand-made repository tarballs, shaped like GitHub's: a pax global header, the top directory, then the entries under it.</summary>
public static class TarballBuilder
{
    public const string DefaultRoot = "acme-widgets-abc1234";

    /// <summary>gzip-compressed tar; entries are added under "acme-widgets-&lt;sha7&gt;/" unless rawNames is true.</summary>
    public static byte[] Build(
        IEnumerable<(string Name, TarEntryType Type, byte[] Content, string? LinkName)> entries,
        bool rawNames = false,
        string root = DefaultRoot)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            if (!rawNames)
            {
                writer.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(
                    new Dictionary<string, string> { ["comment"] = "0123456789abcdef0123456789abcdef01234567" }));
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, root + "/"));
            }

            foreach (var (name, type, content, linkName) in entries)
            {
                var entry = new PaxTarEntry(type, rawNames ? name : $"{root}/{name}");
                if (linkName is not null)
                {
                    entry.LinkName = linkName;
                }

                if (type is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                {
                    entry.DataStream = new MemoryStream(content);
                }

                writer.WriteEntry(entry);
            }
        }

        return output.ToArray();
    }

    public static byte[] Build(params (string Name, string Content)[] files) =>
        Build(files.Select(file => (file.Name, TarEntryType.RegularFile, System.Text.Encoding.UTF8.GetBytes(file.Content), (string?)null)));
}
