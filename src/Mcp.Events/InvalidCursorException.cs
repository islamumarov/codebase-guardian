namespace Mcp.Events;

/// <summary>A cursor could not be decoded, or cannot have been issued by this log.</summary>
public sealed class InvalidCursorException : Exception
{
    public InvalidCursorException(string message) : base(message) { }
    public InvalidCursorException(string message, Exception inner) : base(message, inner) { }
}
