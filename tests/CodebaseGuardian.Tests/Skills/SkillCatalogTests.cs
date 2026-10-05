using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcp.Skills;

namespace CodebaseGuardian.Tests.Skills;

public class SkillCatalogTests
{
    private static SkillCatalog Load(SkillFixture fx) => SkillCatalog.Load(Options(fx));

    private static SkillsOptions Options(SkillFixture fx)
    {
        var o = new SkillsOptions();
        o.Directories.Add(fx.Root);
        return o;
    }

    private static IReadOnlyList<string> LoadErrors(SkillFixture fx) =>
        Assert.Throws<SkillValidationException>(() => Load(fx)).Errors;

    [Fact]
    public void Loads_single_skill_with_independent_digest()
    {
        using var fx = new SkillFixture();
        fx.WriteSkill("git-workflow");
        var catalog = Load(fx);
        var skill = Assert.Single(catalog.Skills);
        Assert.Equal("skill://git-workflow/SKILL.md", skill.Uri);
        Assert.Equal("skill://git-workflow", skill.RootUri);
        Assert.Equal("git-workflow", skill.SkillPath);
        Assert.Equal("git-workflow", skill.Name);
        var file = Assert.Single(skill.Files);
        var bytes = File.ReadAllBytes(Path.Combine(fx.Root, "git-workflow", "SKILL.md"));
        Assert.Equal("sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), file.Digest);
        Assert.Equal(bytes.Length, file.Size);
        Assert.Equal(skill.Uri, file.Uri);
        Assert.Equal("SKILL.md", file.RelativePath);
        Assert.Equal("text/markdown", file.MimeType);
        Assert.Same(file, catalog.FindFile(skill.Uri));
        Assert.Same(skill, catalog.FindSkill(skill.Uri));
    }

    [Fact]
    public void Reproduces_spec_example_size_and_digest()
    {
        using var fx = new SkillFixture();
        fx.WriteFile("pdf-processing/SKILL.md",
            "---\nname: pdf-processing\ndescription: Extract, fill, and assemble PDF documents\n---\n\n# PDF processing\n\nChoose the matching template from `templates/`.\n");
        var file = Assert.Single(Assert.Single(Load(fx).Skills).Files);
        Assert.Equal(151, file.Size);
        Assert.Equal("sha256:99b737495721155ece826d57521e2d66141ebdc1344a400487481ea2642ab19e", file.Digest);
    }

    [Fact]
    public void Prefix_path_names_skill_by_last_segment()
    {
        using var fx = new SkillFixture();
        fx.WriteSkill("acme/billing/refunds");
        var skill = Assert.Single(Load(fx).Skills);
        Assert.Equal("refunds", skill.Name);
        Assert.Equal("acme/billing/refunds", skill.SkillPath);
        Assert.Equal("skill://acme/billing/refunds/SKILL.md", skill.Uri);
    }

    [Fact]
    public void Custom_uri_scheme_is_used()
    {
        using var fx = new SkillFixture();
        fx.WriteSkill("a-skill");
        var o = Options(fx);
        o.UriScheme = "acme";
        Assert.Equal("acme://a-skill/SKILL.md", Assert.Single(SkillCatalog.Load(o).Skills).Uri);
    }

    [Fact]
    public void Nested_skill_files_belong_to_both_skills()
    {
        using var fx = new SkillFixture();
        fx.WriteSkill("outer");
        fx.WriteSkill("outer/inner");
        fx.WriteFile("outer/inner/notes.txt", "n");
        var catalog = Load(fx);
        Assert.Equal(["skill://outer/SKILL.md", "skill://outer/inner/SKILL.md"], catalog.Skills.Select(s => s.Uri).ToArray());
        var outer = catalog.FindSkill("skill://outer/SKILL.md")!;
        Assert.Equal(["skill://outer/SKILL.md", "skill://outer/inner/SKILL.md", "skill://outer/inner/notes.txt"],
            outer.Files.Select(f => f.Uri).ToArray());
        Assert.Equal(2, catalog.FindSkill("skill://outer/inner/SKILL.md")!.Files.Count);
    }

    [Fact]
    public void Frontmatter_is_passed_through_with_json_types()
    {
        using var fx = new SkillFixture();
        fx.WriteSkill("typed", SkillFixture.SkillMd("typed", extra:
            "license: MIT\nmetadata:\n  team: core\nversion: 1.0\nquoted: \"1.0\"\nflag: true\nempty:\n"));
        var fm = Assert.Single(Load(fx).Skills).Frontmatter;
        Assert.Equal("MIT", fm["license"]!.GetValue<string>());
        Assert.Equal("core", fm["metadata"]!["team"]!.GetValue<string>());
        Assert.Equal(JsonValueKind.Number, fm["version"]!.GetValueKind());
        Assert.Equal(JsonValueKind.String, fm["quoted"]!.GetValueKind());
        Assert.Equal(JsonValueKind.True, fm["flag"]!.GetValueKind());
        Assert.True(fm.ContainsKey("empty"));
        Assert.Null(fm["empty"]);
    }

    [Theory]
    [InlineData("PDF-Processing")]
    [InlineData("-pdf")]
    [InlineData("pdf--processing")]
    public void Invalid_names_are_rejected(string name)
    {
        using var fx = new SkillFixture();
        fx.WriteSkill("some-skill", SkillFixture.SkillMd(name));
        Assert.Contains(LoadErrors(fx), e => e.Contains($"'{name}'"));
    }

    [Fact]
    public void Name_longer_than_64_chars_is_rejected()
    {
        using var fx = new SkillFixture();
        var name = new string('a', 65);
        fx.WriteSkill(name);
        Assert.Contains(LoadErrors(fx), e => e.Contains(name));
    }

    [Fact]
    public void Name_must_equal_directory_name()
    {
        using var fx = new SkillFixture();
        fx.WriteSkill("dir-name", SkillFixture.SkillMd("other-name"));
        Assert.Contains(LoadErrors(fx), e => e.Contains("other-name") && e.Contains("dir-name"));
    }

    [Fact]
    public void Invalid_prefix_segment_is_rejected()
    {
        using var fx = new SkillFixture();
        fx.WriteSkill("Acme/refunds", SkillFixture.SkillMd("refunds"));
        Assert.Contains(LoadErrors(fx), e => e.Contains("'Acme'"));
    }

    [Fact]
    public void Skill_md_directly_in_root_is_rejected()
    {
        using var fx = new SkillFixture();
        fx.WriteFile("SKILL.md", SkillFixture.SkillMd("root"));
        Assert.Single(LoadErrors(fx));
    }

    [Fact]
    public void Description_and_frontmatter_problems_are_all_reported_at_once()
    {
        using var fx = new SkillFixture();
        fx.WriteSkill("no-desc", "---\nname: no-desc\n---\n");
        fx.WriteSkill("long-desc", SkillFixture.SkillMd("long-desc", new string('x', 1025)));
        fx.WriteSkill("empty-desc", "---\nname: empty-desc\ndescription: \"\"\n---\n");
        fx.WriteSkill("no-front", "# nothing here\n");
        fx.WriteSkill("bad-compat", SkillFixture.SkillMd("bad-compat", extra: "compatibility: \"\"\n"));
        var errors = LoadErrors(fx);
        Assert.Equal(5, errors.Count);
        foreach (var skill in new[] { "no-desc", "long-desc", "empty-desc", "no-front", "bad-compat" })
            Assert.Contains(errors, e => e.Contains(skill));
    }

    [Fact]
    public void Description_of_exactly_1024_chars_is_accepted()
    {
        using var fx = new SkillFixture();
        fx.WriteSkill("max-desc", SkillFixture.SkillMd("max-desc", new string('x', 1024)));
        Assert.Equal(1024, Assert.Single(Load(fx).Skills).Description.Length);
    }

    [Fact]
    public void Yaml_aliases_are_rejected()
    {
        using var fx = new SkillFixture();
        fx.WriteSkill("aliased", SkillFixture.SkillMd("aliased", extra: "a: &x 1\nb: *x\n"));
        Assert.Contains(LoadErrors(fx), e => e.Contains("alias"));
    }

    [Fact]
    public void Crlf_skill_md_is_hashed_over_raw_bytes()
    {
        using var fx = new SkillFixture();
        var bytes = Encoding.UTF8.GetBytes("---\r\nname: crlf\r\ndescription: d\r\n---\r\n\r\nbody\r\n");
        fx.WriteBytes("crlf/SKILL.md", bytes);
        var file = Assert.Single(Assert.Single(Load(fx).Skills).Files);
        Assert.Equal(bytes, file.Content);
        Assert.Equal("sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), file.Digest);
        Assert.True(file.IsUtf8Text);
    }

    [Fact]
    public void Bom_is_preserved_in_bytes_and_tolerated_in_frontmatter()
    {
        using var fx = new SkillFixture();
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(SkillFixture.SkillMd("bom"))).ToArray();
        fx.WriteBytes("bom/SKILL.md", bytes);
        var skill = Assert.Single(Load(fx).Skills);
        Assert.Equal(bytes, skill.Files[0].Content);
        Assert.Equal("bom", skill.Name);
    }

    [Fact]
    public void Non_utf8_file_is_flagged_binary_with_mime_from_extension()
    {
        using var fx = new SkillFixture();
        fx.WriteSkill("bin-skill");
        fx.WriteBytes("bin-skill/assets/data.csv", [0xFF, 0xFE, 0x00]);
        fx.WriteBytes("bin-skill/assets/blob.xyz", [1, 2, 3]);
        var catalog = Load(fx);
        var csv = catalog.FindFile("skill://bin-skill/assets/data.csv")!;
        Assert.False(csv.IsUtf8Text);
        Assert.Equal("text/csv", csv.MimeType);
        Assert.Equal("application/octet-stream", catalog.FindFile("skill://bin-skill/assets/blob.xyz")!.MimeType);
        Assert.True(catalog.FindFile("skill://bin-skill/SKILL.md")!.IsUtf8Text);
    }

    [Fact]
    public void Hidden_files_and_directories_are_ignored()
    {
        using var fx = new SkillFixture();
        fx.WriteSkill("clean");
        fx.WriteFile("clean/.DS_Store", "junk");
        fx.WriteFile("clean/.git/config", "junk");
        fx.WriteFile("clean/references/.hidden.md", "junk");
        fx.WriteSkill(".hidden-skill", SkillFixture.SkillMd("hidden-skill"));
        var catalog = Load(fx);
        Assert.Equal("skill://clean/SKILL.md", Assert.Single(catalog.Skills).Uri);
        Assert.Single(Assert.Single(catalog.Skills).Files);
    }

    [Fact]
    public void Symbolic_links_are_rejected()
    {
        using var fx = new SkillFixture();
        fx.WriteSkill("linked");
        fx.WriteFile("target.txt", "t");
        try
        {
            File.CreateSymbolicLink(Path.Combine(fx.Root, "linked", "link.txt"), Path.Combine(fx.Root, "target.txt"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // platform cannot create symlinks
        }
        Assert.Contains(LoadErrors(fx), e => e.Contains("link.txt") && e.Contains("ymbolic"));
    }

    [Fact]
    public void Invalid_file_path_segment_is_rejected()
    {
        using var fx = new SkillFixture();
        fx.WriteSkill("odd-files");
        fx.WriteFile("odd-files/my file.md", "x");
        Assert.Contains(LoadErrors(fx), e => e.Contains("my file.md"));
    }

    [Fact]
    public void Directory_listing_returns_direct_children_only()
    {
        using var fx = new SkillFixture();
        fx.WriteSkill("listed");
        fx.WriteFile("listed/references/GUIDE.md", "guide");
        fx.WriteFile("listed/references/deep/more.md", "more");
        var catalog = Load(fx);

        Assert.True(catalog.TryListDirectory("skill://listed", out var root));
        Assert.Equal(["skill://listed/SKILL.md", "skill://listed/references"], root.Select(e => e.Uri).ToArray());
        Assert.Equal(new SkillDirectoryEntry("skill://listed/references", "references", true, "inode/directory", null), root[1]);
        Assert.False(root[0].IsDirectory);
        Assert.Equal("text/markdown", root[0].MimeType);
        Assert.Equal(catalog.FindFile("skill://listed/SKILL.md")!.Size, root[0].Size);

        Assert.True(catalog.TryListDirectory("skill://listed/references", out var refs));
        Assert.Equal(["skill://listed/references/GUIDE.md", "skill://listed/references/deep"], refs.Select(e => e.Uri).ToArray());
        Assert.True(catalog.TryListDirectory("skill://listed/references/deep", out var deep));
        Assert.Equal("more.md", Assert.Single(deep).Name);
    }

    [Fact]
    public void Directory_listing_fails_for_files_and_unknown_uris()
    {
        using var fx = new SkillFixture();
        fx.WriteSkill("listed");
        var catalog = Load(fx);
        Assert.False(catalog.TryListDirectory("skill://listed/SKILL.md", out var children));
        Assert.Empty(children);
        Assert.False(catalog.TryListDirectory("skill://nope", out _));
        Assert.False(catalog.TryListDirectory("skill://listed/", out _));
    }

    [Fact]
    public void More_than_512_files_is_an_error()
    {
        using var fx = new SkillFixture();
        fx.WriteSkill("big");
        for (var i = 0; i < 512; i++) fx.WriteFile($"big/f/{i}.txt", "x");
        Assert.Contains(LoadErrors(fx), e => e.Contains("513"));
    }

    [Fact]
    public void Exactly_512_files_is_accepted()
    {
        using var fx = new SkillFixture();
        fx.WriteSkill("big");
        for (var i = 0; i < 511; i++) fx.WriteFile($"big/f/{i}.txt", "x");
        Assert.Equal(512, Assert.Single(Load(fx).Skills).Files.Count);
    }

    [Fact]
    public void Total_size_over_16_mib_is_an_error()
    {
        using var fx = new SkillFixture();
        fx.WriteSkill("heavy");
        fx.WriteBytes("heavy/big.bin", new byte[16 * 1024 * 1024]);
        Assert.Contains(LoadErrors(fx), e => e.Contains("heavy") && e.Contains("bytes"));
    }

    [Fact]
    public void Missing_root_directory_is_an_error_collected_with_others()
    {
        using var fx = new SkillFixture();
        fx.WriteSkill("bad", "no frontmatter");
        var missing = Path.Combine(fx.Root, "does-not-exist");
        var o = Options(fx);
        o.Directories.Add(missing);
        var errors = Assert.Throws<SkillValidationException>(() => SkillCatalog.Load(o)).Errors;
        Assert.Contains($"Skills directory not found: {missing}", errors);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void Multiple_roots_are_merged_and_duplicates_rejected()
    {
        using var a = new SkillFixture();
        using var b = new SkillFixture();
        a.WriteSkill("zeta");
        b.WriteSkill("alpha");
        var o = new SkillsOptions();
        o.Directories.Add(a.Root);
        o.Directories.Add(b.Root);
        Assert.Equal(["skill://alpha/SKILL.md", "skill://zeta/SKILL.md"], SkillCatalog.Load(o).Skills.Select(s => s.Uri).ToArray());

        b.WriteSkill("zeta");
        var ex = Assert.Throws<SkillValidationException>(() => SkillCatalog.Load(o));
        Assert.Contains(ex.Errors, e => e.Contains("Duplicate") && e.Contains("skill://zeta/SKILL.md"));
    }

    [Fact]
    public void Exception_message_lists_every_error()
    {
        var ex = new SkillValidationException(["one", "two"]);
        Assert.Contains("- one", ex.Message);
        Assert.Contains("- two", ex.Message);
    }
}
