using Mcp.Events;

namespace CodebaseGuardian.Tests.Events;

public class EventCursorTests
{
    private static string B64(string s) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void RoundTrips()
    {
        var epoch = Guid.NewGuid();
        var cursor = EventCursor.Encode(epoch, 42);
        var (e, s) = EventCursor.Decode(cursor);
        Assert.Equal(epoch, e);
        Assert.Equal(42, s);
    }

    [Fact]
    public void EncodesDocumentedFormat()
    {
        var epoch = Guid.NewGuid();
        var cursor = EventCursor.Encode(epoch, 7);
        Assert.DoesNotContain('=', cursor);
        Assert.DoesNotContain('+', cursor);
        Assert.DoesNotContain('/', cursor);
        Assert.Equal(B64($"v1:{epoch:N}:7"), cursor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("!!!not base64!!!")]
    [InlineData("a")]                       // invalid base64 length
    public void RejectsUndecodableBase64(string cursor) =>
        Assert.Throws<InvalidCursorException>(() => EventCursor.Decode(cursor));

    [Fact]
    public void RejectsStructuralDefects()
    {
        var g = Guid.NewGuid().ToString("N");
        foreach (var raw in new[]
        {
            $"v2:{g}:1",          // wrong version
            $"{g}:1",             // missing version
            $"v1:{g}",            // too few fields
            $"v1:{g}:1:2",        // too many fields
            "v1:nothex:1",        // bad epoch
            $"v1:{Guid.NewGuid()}:1", // epoch not in N format (dashes)
            $"v1:{g}:abc",        // non numeric
            $"v1:{g}:-1",         // negative
            $"v1:{g}:+1",         // sign
            $"v1:{g}: 1",         // whitespace
            $"v1:{g}:",           // empty sequence
            $"v1:{g}:99999999999999999999", // overflow
        })
        {
            Assert.Throws<InvalidCursorException>(() => EventCursor.Decode(B64(raw)));
        }
    }

    [Fact]
    public void RejectsNonCanonicalEncoding()
    {
        var cursor = EventCursor.Encode(Guid.NewGuid(), 1);
        Assert.Throws<InvalidCursorException>(() => EventCursor.Decode(cursor + "="));
        Assert.Throws<InvalidCursorException>(() => EventCursor.Decode(" " + cursor));
    }
}
