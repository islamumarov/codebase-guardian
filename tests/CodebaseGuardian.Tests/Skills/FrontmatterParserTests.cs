using System.Text.Json;
using System.Text.Json.Nodes;
using Mcp.Skills;

namespace CodebaseGuardian.Tests.Skills;

public class FrontmatterParserTests
{
    [Fact]
    public void Applies_core_schema_typing_and_preserves_key_order()
    {
        var fm = FrontmatterParser.Parse(
            "---\nname: x\nversion: 1.0\nquoted: \"1.0\"\nflag: true\nno: False\nempty:\ntilde: ~\nnul: null\ncount: 42\nhex: 0x1F\nneg: -3\nsci: 1e3\ndate: 2026-10-04\nlist: [a, 1, true]\nmetadata: {team: core}\n---\nbody");
        Assert.Equal(["name", "version", "quoted", "flag", "no", "empty", "tilde", "nul", "count", "hex", "neg", "sci", "date", "list", "metadata"],
            fm.Select(p => p.Key).ToArray());
        Assert.Equal(JsonValueKind.Number, fm["version"]!.GetValueKind());
        Assert.Equal(1.0, fm["version"]!.GetValue<double>());
        Assert.Equal(JsonValueKind.String, fm["quoted"]!.GetValueKind());
        Assert.Equal("1.0", fm["quoted"]!.GetValue<string>());
        Assert.True(fm["flag"]!.GetValue<bool>());
        Assert.False(fm["no"]!.GetValue<bool>());
        Assert.Null(fm["empty"]);
        Assert.Null(fm["tilde"]);
        Assert.Null(fm["nul"]);
        Assert.Equal(42, fm["count"]!.GetValue<long>());
        Assert.Equal(31, fm["hex"]!.GetValue<long>());
        Assert.Equal(-3, fm["neg"]!.GetValue<long>());
        Assert.Equal(1000.0, fm["sci"]!.GetValue<double>());
        Assert.Equal("2026-10-04", fm["date"]!.GetValue<string>());
        Assert.Equal("""["a",1,true]""", fm["list"]!.ToJsonString());
        Assert.Equal("core", fm["metadata"]!["team"]!.GetValue<string>());
    }

    [Fact]
    public void Quoted_scalars_are_always_strings()
    {
        var fm = FrontmatterParser.Parse("---\na: 'true'\nb: \"null\"\nc: \"\"\n---\n");
        Assert.Equal("true", fm["a"]!.GetValue<string>());
        Assert.Equal("null", fm["b"]!.GetValue<string>());
        Assert.Equal("", fm["c"]!.GetValue<string>());
    }

    [Fact]
    public void Tolerates_bom_and_crlf()
    {
        var fm = FrontmatterParser.Parse("﻿---\r\nname: a\r\ndescription: b c\r\n---\r\n\r\nbody\r\n");
        Assert.Equal("a", fm["name"]!.GetValue<string>());
        Assert.Equal("b c", fm["description"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("# no frontmatter\n")]
    [InlineData("---\nname: a\n")]
    [InlineData("---\n---\n")]
    [InlineData("---\n- a\n- b\n---\n")]
    [InlineData("---\nname: [unclosed\n---\n")]
    [InlineData("---\nname: a\nname: b\n---\n")]
    public void Rejects_missing_or_invalid_frontmatter(string input) =>
        Assert.Throws<FormatException>(() => FrontmatterParser.Parse(input));

    [Theory]
    [InlineData("---\nbase: &b {x: 1}\nother: *b\n---\n")]
    [InlineData("---\nbase: &b value\n---\n")]
    [InlineData("---\nlist: &l [1]\n---\n")]
    public void Rejects_anchors_and_aliases(string input) =>
        Assert.Throws<FormatException>(() => FrontmatterParser.Parse(input));
}
