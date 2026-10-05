using System.Text.Json.Nodes;
using Mcp.Events;

namespace CodebaseGuardian.Tests.Events;

public class ArgumentValidatorTests
{
    private static JsonObject Schema() => JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "branch": { "type": "string" },
            "limit": { "type": "integer" },
            "ratio": { "type": "number" },
            "flag": { "type": "boolean" },
            "tags": { "type": "array" },
            "extra": { "type": "object" },
            "level": { "type": "string", "enum": ["low", "high"] }
          },
          "required": ["branch"]
        }
        """)!.AsObject();

    private static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void ValidArgumentsReturnNull() =>
        Assert.Null(ArgumentValidator.Validate(Schema(), Args("""{"branch":"main","limit":3,"ratio":1.5,"flag":true,"tags":[],"extra":{},"level":"low"}""")));

    [Fact]
    public void NullArgumentsWithNoRequiredAreValid() =>
        Assert.Null(ArgumentValidator.Validate(new JsonObject { ["type"] = "object" }, null));

    [Fact]
    public void NullArgumentsWithRequiredAreInvalid() =>
        Assert.Contains("branch", ArgumentValidator.Validate(Schema(), null));

    [Fact]
    public void MissingRequiredIsReported() =>
        Assert.Contains("branch", ArgumentValidator.Validate(Schema(), Args("{}")));

    [Theory]
    [InlineData("""{"branch":1}""", "branch")]
    [InlineData("""{"branch":"m","limit":1.5}""", "limit")]
    [InlineData("""{"branch":"m","limit":"3"}""", "limit")]
    [InlineData("""{"branch":"m","ratio":"x"}""", "ratio")]
    [InlineData("""{"branch":"m","flag":"true"}""", "flag")]
    [InlineData("""{"branch":"m","tags":{}}""", "tags")]
    [InlineData("""{"branch":"m","extra":[]}""", "extra")]
    [InlineData("""{"branch":null}""", "branch")]
    public void TypeMismatchIsReported(string json, string property) =>
        Assert.Contains(property, ArgumentValidator.Validate(Schema(), Args(json)));

    [Fact]
    public void IntegerAcceptsWholeNumberWrittenAsFloat() =>
        Assert.Null(ArgumentValidator.Validate(Schema(), Args("""{"branch":"m","limit":3.0}""")));

    [Fact]
    public void EnumViolationIsReported() =>
        Assert.Contains("level", ArgumentValidator.Validate(Schema(), Args("""{"branch":"m","level":"mid"}""")));

    [Fact]
    public void UndeclaredPropertyIsReported() =>
        Assert.Contains("bogus", ArgumentValidator.Validate(Schema(), Args("""{"branch":"m","bogus":1}""")));

    [Fact]
    public void SchemaWithoutPropertiesRejectsAnyArgument() =>
        Assert.Contains("x", ArgumentValidator.Validate(new JsonObject { ["type"] = "object" }, Args("""{"x":1}""")));
}
