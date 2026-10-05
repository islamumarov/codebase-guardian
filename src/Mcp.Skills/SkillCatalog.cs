using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Mcp.Skills;

public sealed class SkillCatalog
{
    private const int MaxFilesPerSkill = 512;
    private const long MaxBytesPerSkill = 16L * 1024 * 1024;
    private const string SkillFileName = "SKILL.md";

    private readonly Dictionary<string, SkillDefinition> _skillsByUri;
    private readonly Dictionary<string, SkillFile> _filesByUri;
    private readonly Dictionary<string, SortedDictionary<string, SkillDirectoryEntry>> _directories;

    private SkillCatalog(
        IReadOnlyList<SkillDefinition> skills,
        Dictionary<string, SkillFile> files,
        Dictionary<string, SortedDictionary<string, SkillDirectoryEntry>> directories)
    {
        Skills = skills;
        _skillsByUri = skills.ToDictionary(s => s.Uri, StringComparer.Ordinal);
        _filesByUri = files;
        _directories = directories;
    }

    public IReadOnlyList<SkillDefinition> Skills { get; }

    public SkillDefinition? FindSkill(string skillMdUri) => _skillsByUri.GetValueOrDefault(skillMdUri);

    public SkillFile? FindFile(string uri) => _filesByUri.GetValueOrDefault(uri);

    public bool TryListDirectory(string directoryUri, out IReadOnlyList<SkillDirectoryEntry> children)
    {
        if (_directories.TryGetValue(directoryUri, out var entries))
        {
            children = entries.Values.ToList();
            return true;
        }
        children = [];
        return false;
    }

    public static SkillCatalog Load(SkillsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        var scheme = options.UriScheme;
        var skills = new Dictionary<string, SkillDefinition>(StringComparer.Ordinal);
        var bytesCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var root in options.Directories)
        {
            if (!Directory.Exists(root))
            {
                errors.Add($"Skills directory not found: {root}");
                continue;
            }

            var files = new List<(string Rel, string Full)>();
            Walk(root, "", files, errors);

            foreach (var skillMd in files.Where(f => f.Rel == SkillFileName || f.Rel.EndsWith("/" + SkillFileName, StringComparison.Ordinal)))
            {
                var skill = LoadSkill(root, skillMd, files, scheme, bytesCache, errors);
                if (skill is null) continue;
                if (!skills.TryAdd(skill.Uri, skill))
                    errors.Add($"Duplicate skill URI {skill.Uri} (second definition in {root})");
            }
        }

        if (errors.Count > 0)
            throw new SkillValidationException(errors);

        var sorted = skills.Values.OrderBy(s => s.Uri, StringComparer.Ordinal).ToList();
        var fileIndex = new Dictionary<string, SkillFile>(StringComparer.Ordinal);
        var dirIndex = new Dictionary<string, SortedDictionary<string, SkillDirectoryEntry>>(StringComparer.Ordinal);
        foreach (var skill in sorted)
        {
            foreach (var file in skill.Files)
            {
                fileIndex.TryAdd(file.Uri, file);
                var segments = file.RelativePath.Split('/');
                var dirUri = skill.RootUri;
                for (var i = 0; i < segments.Length; i++)
                {
                    var childUri = dirUri + "/" + segments[i];
                    var isDir = i < segments.Length - 1;
                    var entries = GetDir(dirIndex, dirUri);
                    entries.TryAdd(childUri, isDir
                        ? new SkillDirectoryEntry(childUri, segments[i], true, MimeTypes.Directory, null)
                        : new SkillDirectoryEntry(childUri, segments[i], false, file.MimeType, file.Size));
                    if (isDir) GetDir(dirIndex, childUri);
                    dirUri = childUri;
                }
            }
        }
        return new SkillCatalog(sorted, fileIndex, dirIndex);
    }

    private static SortedDictionary<string, SkillDirectoryEntry> GetDir(
        Dictionary<string, SortedDictionary<string, SkillDirectoryEntry>> index, string uri)
    {
        if (!index.TryGetValue(uri, out var entries))
            index[uri] = entries = new SortedDictionary<string, SkillDirectoryEntry>(StringComparer.Ordinal);
        return entries;
    }

    private static void Walk(string dir, string rel, List<(string Rel, string Full)> files, List<string> errors)
    {
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            if (entry.Name.StartsWith('.')) continue;
            var entryRel = rel.Length == 0 ? entry.Name : rel + "/" + entry.Name;
            if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                errors.Add($"Symbolic links are not allowed: {entry.FullName}");
                continue;
            }
            if (entry is DirectoryInfo)
                Walk(entry.FullName, entryRel, files, errors);
            else
                files.Add((entryRel, entry.FullName));
        }
    }

    private static SkillDefinition? LoadSkill(
        string root,
        (string Rel, string Full) skillMd,
        List<(string Rel, string Full)> allFiles,
        string scheme,
        Dictionary<string, byte[]> bytesCache,
        List<string> errors)
    {
        var skillPath = skillMd.Rel == SkillFileName ? "" : skillMd.Rel[..^(SkillFileName.Length + 1)];
        var where = skillMd.Full;
        if (skillPath.Length == 0)
        {
            errors.Add($"{where}: SKILL.md directly in a skills root has no skill name segment");
            return null;
        }

        var ok = true;
        var segments = skillPath.Split('/');
        foreach (var seg in segments.Where(s => !SkillNameRules.IsValidName(s)))
        {
            errors.Add($"{where}: skill path segment '{seg}' is not a valid skill name");
            ok = false;
        }

        var prefix = skillPath + "/";
        var members = allFiles
            .Where(f => f.Rel.StartsWith(prefix, StringComparison.Ordinal))
            .Select(f => (Rel: f.Rel[prefix.Length..], f.Full))
            .ToList();

        foreach (var m in members)
        {
            foreach (var seg in m.Rel.Split('/').Where(s => !SkillNameRules.IsValidFileSegment(s)))
            {
                errors.Add($"{where}: file '{m.Rel}' has an invalid path segment '{seg}'");
                ok = false;
            }
        }

        var totalSize = members.Sum(m => new FileInfo(m.Full).Length);
        if (members.Count > MaxFilesPerSkill)
        {
            errors.Add($"{where}: skill has {members.Count} files (limit {MaxFilesPerSkill})");
            ok = false;
        }
        if (totalSize > MaxBytesPerSkill)
        {
            errors.Add($"{where}: skill totals {totalSize} bytes (limit {MaxBytesPerSkill})");
            ok = false;
        }

        // Frontmatter
        JsonObject? frontmatter = null;
        string? name = null, description = null;
        try
        {
            var skillText = new UTF8Encoding(false, true).GetString(ReadBytes(skillMd.Full, bytesCache));
            frontmatter = FrontmatterParser.Parse(skillText);
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            errors.Add($"{where}: {ex.Message}");
            ok = false;
        }

        if (frontmatter is not null)
        {
            name = StringField(frontmatter, "name");
            if (name is null)
            {
                errors.Add($"{where}: frontmatter 'name' is missing or not a string");
                ok = false;
            }
            else if (!SkillNameRules.IsValidName(name))
            {
                errors.Add($"{where}: frontmatter name '{name}' is not a valid skill name");
                ok = false;
            }
            else if (name != segments[^1])
            {
                errors.Add($"{where}: frontmatter name '{name}' must equal the directory name '{segments[^1]}'");
                ok = false;
            }

            description = StringField(frontmatter, "description");
            if (string.IsNullOrEmpty(description) || description.Length > SkillNameRules.MaxDescriptionLength)
            {
                errors.Add($"{where}: frontmatter 'description' must be 1-{SkillNameRules.MaxDescriptionLength} characters");
                ok = false;
            }

            if (frontmatter.ContainsKey("compatibility"))
            {
                var compat = StringField(frontmatter, "compatibility");
                if (compat is null || compat.Length is < 1 or > SkillNameRules.MaxCompatibilityLength)
                {
                    errors.Add($"{where}: frontmatter 'compatibility' must be 1-{SkillNameRules.MaxCompatibilityLength} characters");
                    ok = false;
                }
            }
        }

        if (!ok) return null;

        var rootUri = scheme + "://" + skillPath;
        var skillFiles = members
            .Select(m => BuildFile(rootUri, m.Rel, ReadBytes(m.Full, bytesCache)))
            .OrderBy(f => f.Uri, StringComparer.Ordinal)
            .ToList();
        return new SkillDefinition(rootUri + "/" + SkillFileName, rootUri, skillPath, name!, description!, frontmatter!, skillFiles);
    }

    private static string? StringField(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static byte[] ReadBytes(string path, Dictionary<string, byte[]> cache)
    {
        if (!cache.TryGetValue(path, out var bytes))
            cache[path] = bytes = File.ReadAllBytes(path);
        return bytes;
    }

    private static SkillFile BuildFile(string rootUri, string rel, byte[] content)
    {
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        return new SkillFile(rootUri + "/" + rel, rel, content, digest, content.Length, MimeTypes.FromPath(rel), IsUtf8Text(content));
    }

    private static bool IsUtf8Text(byte[] content)
    {
        try
        {
            var enc = new UTF8Encoding(false, throwOnInvalidBytes: true);
            return enc.GetBytes(enc.GetString(content)).AsSpan().SequenceEqual(content);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
