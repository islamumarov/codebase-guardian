using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Mcp.Skills;

public static partial class FrontmatterParser
{
    [GeneratedRegex(@"^[-+]?[0-9]+$")]
    private static partial Regex IntRegex();

    [GeneratedRegex(@"^0o[0-7]+$")]
    private static partial Regex OctRegex();

    [GeneratedRegex(@"^0x[0-9a-fA-F]+$")]
    private static partial Regex HexRegex();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex FloatRegex();

    /// <summary>Extracts the leading '---' YAML block and converts it to JSON using YAML 1.2 core-schema typing.</summary>
    public static JsonObject Parse(string skillMarkdown)
    {
        ArgumentNullException.ThrowIfNull(skillMarkdown);
        var text = skillMarkdown.TrimStart('﻿');
        var lines = text.Split('\n');
        if (lines[0].TrimEnd() != "---")
            throw new FormatException("SKILL.md must begin with a '---' YAML frontmatter block.");

        var end = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == "---") { end = i; break; }
        }
        if (end < 0)
            throw new FormatException("Frontmatter block is not terminated by a closing '---' line.");

        var yaml = string.Join("\n", lines[1..end].Select(l => l.TrimEnd('\r')));
        try
        {
            return ConvertDocument(yaml);
        }
        catch (OverflowException ex)
        {
            throw new FormatException("Numeric frontmatter value out of range.", ex);
        }
        catch (YamlException ex)
        {
            throw new FormatException("Invalid YAML frontmatter: " + ex.Message, ex);
        }
    }

    private static JsonObject ConvertDocument(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        parser.Consume<StreamStart>();
        if (!parser.TryConsume<DocumentStart>(out _))
            throw new FormatException("Frontmatter is empty; a mapping with 'name' and 'description' is required.");
        if (parser.Current is not MappingStart)
            throw new FormatException("Frontmatter must be a YAML mapping.");

        var node = ReadNode(parser);
        parser.Consume<DocumentEnd>();
        return (JsonObject)node!;
    }

    private static JsonNode? ReadNode(Parser parser)
    {
        switch (parser.Current)
        {
            case Scalar scalar:
                RejectAnchor(scalar.Anchor);
                parser.MoveNext();
                return ConvertScalar(scalar);
            case SequenceStart seq:
                RejectAnchor(seq.Anchor);
                parser.MoveNext();
                var array = new JsonArray();
                while (parser.Current is not SequenceEnd)
                    array.Add(ReadNode(parser));
                parser.MoveNext();
                return array;
            case MappingStart map:
                RejectAnchor(map.Anchor);
                parser.MoveNext();
                var obj = new JsonObject();
                while (parser.Current is not MappingEnd)
                {
                    if (parser.Current is not Scalar key)
                        throw new FormatException("Frontmatter mapping keys must be scalars.");
                    RejectAnchor(key.Anchor);
                    parser.MoveNext();
                    var value = ReadNode(parser);
                    if (!obj.TryAdd(key.Value, value))
                        throw new FormatException($"Duplicate frontmatter key '{key.Value}'.");
                }
                parser.MoveNext();
                return obj;
            case AnchorAlias:
                throw new FormatException("YAML aliases and anchors are not allowed in frontmatter.");
            default:
                throw new FormatException("Unexpected YAML content in frontmatter.");
        }
    }

    private static void RejectAnchor(AnchorName anchor)
    {
        if (!anchor.IsEmpty)
            throw new FormatException("YAML aliases and anchors are not allowed in frontmatter.");
    }

    private static JsonNode? ConvertScalar(Scalar scalar)
    {
        var value = scalar.Value;
        if (scalar.Style != ScalarStyle.Plain)
            return JsonValue.Create(value);
        if (!scalar.Tag.IsEmpty)
            return JsonValue.Create(value); // explicit tags other than plain resolution: keep as string
        if (value.Length == 0 || value is "null" or "Null" or "NULL" or "~")
            return null;
        if (value is "true" or "True" or "TRUE")
            return JsonValue.Create(true);
        if (value is "false" or "False" or "FALSE")
            return JsonValue.Create(false);
        if (IntRegex().IsMatch(value))
        {
            if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                return JsonValue.Create(l);
            return JsonValue.Create(double.Parse(value, CultureInfo.InvariantCulture));
        }
        if (OctRegex().IsMatch(value))
            return JsonValue.Create(Convert.ToInt64(value[2..], 8));
        if (HexRegex().IsMatch(value))
            return JsonValue.Create(Convert.ToInt64(value[2..], 16));
        if (FloatRegex().IsMatch(value))
            return JsonValue.Create(double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture));
        return JsonValue.Create(value); // includes .inf/.nan, which JSON cannot represent
    }
}
