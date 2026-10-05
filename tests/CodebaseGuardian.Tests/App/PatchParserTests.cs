using CodebaseGuardian.Security;

namespace CodebaseGuardian.Tests.App;

public class PatchParserTests
{
    private const string Patch = """
        diff --git a/one.txt b/one.txt
        index 111..222 100644
        --- a/one.txt
        +++ b/one.txt
        @@ -1,3 +1,4 @@
         keep1
        -removed
        +added-a
         keep2
        +added-b
        @@ -10,2 +11,3 @@ context
         keep3
        +added-c
         keep4
        diff --git a/two.txt b/two.txt
        new file mode 100644
        --- /dev/null
        +++ b/two.txt
        @@ -0,0 +1,2 @@
        +first
        +++ b/not-a-header
        diff --git a/gone.txt b/gone.txt
        deleted file mode 100644
        --- a/gone.txt
        +++ /dev/null
        @@ -1 +0,0 @@
        -bye
        """;

    [Fact]
    public void Added_lines_get_new_side_numbers_across_hunks_and_files()
    {
        var added = PatchParser.AddedLines(Patch).ToList();

        Assert.Equal(
            [("one.txt", 2, "added-a"), ("one.txt", 4, "added-b"), ("one.txt", 12, "added-c"), ("two.txt", 1, "first"), ("two.txt", 2, "++ b/not-a-header")],
            added.Select(a => (a.Path, a.Line, a.Text)));
    }

    [Fact]
    public void Removed_lines_and_deleted_files_yield_nothing() =>
        Assert.Empty(PatchParser.AddedLines("diff --git a/g b/g\n--- a/g\n+++ /dev/null\n@@ -1,2 +0,0 @@\n-x\n-y\n"));

    [Fact]
    public void An_empty_patch_yields_nothing() => Assert.Empty(PatchParser.AddedLines(""));
}
