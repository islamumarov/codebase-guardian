namespace Mcp.Skills;

public static class MimeTypes
{
    public const string Directory = "inode/directory";

    public static string FromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".md" => "text/markdown",
        ".txt" => "text/plain",
        ".json" => "application/json",
        ".yaml" or ".yml" => "application/yaml",
        ".py" => "text/x-python",
        ".sh" => "text/x-shellscript",
        ".cs" => "text/x-csharp",
        ".js" => "text/javascript",
        ".ts" => "text/typescript",
        ".csv" => "text/csv",
        ".html" => "text/html",
        ".png" => "image/png",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };
}
