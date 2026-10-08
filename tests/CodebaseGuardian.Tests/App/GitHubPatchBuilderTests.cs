using System.Text;
using CodebaseGuardian.Git;
using CodebaseGuardian.GitHub;
using CodebaseGuardian.Security;
using CodebaseGuardian.Sources;

namespace CodebaseGuardian.Tests.App;

public class GitHubPatchBuilderTests
{
    private const int NoCap = 1024 * 1024;

    private static GitHubFileChange File(string path, string status, int additions, int deletions, string? patch, string? previous = null) =>
        new(path, previous, status, additions, deletions, patch);

    private static void AssertSummary(
        DiffSummary summary, FileDiffStat[] files, int insertions, int deletions, string patch, bool truncated)
    {
        Assert.Equal("from-sha", summary.From);
        Assert.Equal("to-sha", summary.To);
        Assert.Equal(files, summary.Files);
        Assert.Equal(insertions, summary.Insertions);
        Assert.Equal(deletions, summary.Deletions);
        Assert.Equal(patch, summary.Patch);
        Assert.Equal(truncated, summary.PatchTruncated);
    }

    private static DiffSummary Build(IReadOnlyList<GitHubFileChange> files, int maxPatchBytes = NoCap, bool fileListCapped = false) =>
        GitHubPatchBuilder.Build("from-sha", "to-sha", files, maxPatchBytes, fileListCapped);

    [Fact]
    public void An_added_file_is_diffed_against_dev_null_and_its_added_lines_parse()
    {
        var summary = Build([File("src/new.cs", "added", 2, 0, "@@ -0,0 +1,2 @@\n+one\n+two")]);

        AssertSummary(summary, [new FileDiffStat("src/new.cs", 2, 0, false)], 2, 0,
            "diff --git a/src/new.cs b/src/new.cs\n--- /dev/null\n+++ b/src/new.cs\n@@ -0,0 +1,2 @@\n+one\n+two\n", truncated: false);
        Assert.Equal(
            [new AddedLine("src/new.cs", 1, "one"), new AddedLine("src/new.cs", 2, "two")],
            PatchParser.AddedLines(summary.Patch));
    }

    [Fact]
    public void A_removed_file_is_diffed_to_dev_null_and_adds_nothing()
    {
        var summary = Build([File("old.txt", "removed", 0, 1, "@@ -1 +0,0 @@\n-gone\n")]);

        AssertSummary(summary, [new FileDiffStat("old.txt", 0, 1, false)], 0, 1,
            "diff --git a/old.txt b/old.txt\n--- a/old.txt\n+++ /dev/null\n@@ -1 +0,0 @@\n-gone\n", truncated: false);
        Assert.Empty(PatchParser.AddedLines(summary.Patch));
    }

    [Fact]
    public void A_renamed_file_names_the_previous_path_as_the_old_side()
    {
        var summary = Build([File("b.txt", "renamed", 1, 1, "@@ -1 +1 @@\n-x\n+y", previous: "a.txt")]);

        AssertSummary(summary, [new FileDiffStat("b.txt", 1, 1, false)], 1, 1,
            "diff --git a/a.txt b/b.txt\n--- a/a.txt\n+++ b/b.txt\n@@ -1 +1 @@\n-x\n+y\n", truncated: false);
        Assert.Equal([new AddedLine("b.txt", 1, "y")], PatchParser.AddedLines(summary.Patch));
    }

    [Fact]
    public void A_file_without_a_patch_or_counts_is_binary_and_left_out_of_the_totals()
    {
        var summary = Build(
        [
            File("logo.png", "modified", 0, 0, null),
            File("a.txt", "modified", 1, 0, "@@ -1,0 +2 @@\n+added"),
        ]);

        AssertSummary(summary, [new FileDiffStat("logo.png", null, null, true), new FileDiffStat("a.txt", 1, 0, false)], 1, 0,
            "diff --git a/logo.png b/logo.png\n--- a/logo.png\n+++ b/logo.png\n"
            + "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1,0 +2 @@\n+added\n",
            truncated: false);
        Assert.Equal([new AddedLine("a.txt", 2, "added")], PatchParser.AddedLines(summary.Patch));
    }

    [Fact]
    public void A_missing_patch_with_counts_keeps_the_counts_and_marks_the_patch_truncated()
    {
        var summary = Build([File("big.json", "modified", 5000, 10, null)]);

        AssertSummary(summary, [new FileDiffStat("big.json", 5000, 10, false)], 5000, 10,
            "diff --git a/big.json b/big.json\n--- a/big.json\n+++ b/big.json\n", truncated: true);
        Assert.Empty(PatchParser.AddedLines(summary.Patch));
    }

    [Fact]
    public void A_capped_file_list_marks_the_patch_truncated()
    {
        var files = Enumerable.Range(0, 300).Select(i => File($"f{i}.txt", "added", 1, 0, "@@ -0,0 +1 @@\n+x")).ToList();

        var summary = Build(files, fileListCapped: true);

        Assert.True(summary.PatchTruncated);
        Assert.Equal(300, summary.Files.Count);
        Assert.Equal(300, summary.Insertions);
        Assert.Equal(files.Select(f => new AddedLine(f.Path, 1, "x")), PatchParser.AddedLines(summary.Patch));
    }

    [Fact]
    public void A_patch_over_the_byte_cap_is_cut_at_the_limit_like_local_mode()
    {
        const string first = "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -0,0 +1,2 @@\n+café\n+crème\n";
        var files = new[]
        {
            File("a.txt", "modified", 2, 0, "@@ -0,0 +1,2 @@\n+café\n+crème\n"),
            File("b.txt", "added", 1, 0, "@@ -0,0 +1 @@\n+b"),
        };

        // The limit counts UTF-8 bytes (é and è are two each) and ends inside the next "diff --git" line: cut mid-line there.
        var summary = Build(files, maxPatchBytes: Encoding.UTF8.GetByteCount(first) + 5);

        AssertSummary(summary, [new FileDiffStat("a.txt", 2, 0, false), new FileDiffStat("b.txt", 1, 0, false)], 3, 0, first + "diff ", truncated: true);
        Assert.Equal([new AddedLine("a.txt", 1, "café"), new AddedLine("a.txt", 2, "crème")], PatchParser.AddedLines(summary.Patch));
    }

    [Fact]
    public void A_byte_cap_inside_a_multibyte_character_cuts_before_it()
    {
        const string head = "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -0,0 +1 @@\n+caf";

        // One byte into the two-byte é: the cut backs off to the character boundary, as GitRepository.CapPatch does.
        var summary = Build([File("a.txt", "modified", 1, 0, "@@ -0,0 +1 @@\n+café")], maxPatchBytes: Encoding.UTF8.GetByteCount(head) + 1);

        AssertSummary(summary, [new FileDiffStat("a.txt", 1, 0, false)], 1, 0, head, truncated: true);
        Assert.Equal([new AddedLine("a.txt", 1, "caf")], PatchParser.AddedLines(summary.Patch));
    }

    [Fact]
    public void A_patch_that_fits_the_byte_cap_exactly_is_not_truncated()
    {
        const string patch = "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -0,0 +1 @@\n+é\n";

        var summary = Build([File("a.txt", "modified", 1, 0, "@@ -0,0 +1 @@\n+é")], maxPatchBytes: Encoding.UTF8.GetByteCount(patch));

        Assert.Equal(patch, summary.Patch);
        Assert.False(summary.PatchTruncated);
    }

    [Fact]
    public void A_zero_byte_cap_returns_the_stats_with_an_empty_truncated_patch()
    {
        var summary = Build([File("a.txt", "modified", 1, 2, "@@ -1,2 +1 @@\n-a\n-b\n+c")], maxPatchBytes: 0);

        AssertSummary(summary, [new FileDiffStat("a.txt", 1, 2, false)], 1, 2, string.Empty, truncated: true);
        Assert.Empty(PatchParser.AddedLines(summary.Patch));
    }

    [Fact]
    public void A_zero_byte_cap_on_an_empty_diff_is_not_truncated()
    {
        var summary = Build([], maxPatchBytes: 0);

        AssertSummary(summary, [], 0, 0, string.Empty, truncated: false);
    }

    [Theory]
    [InlineData("renamed")]
    [InlineData("copied")]
    public void A_rename_or_copy_without_content_changes_is_not_binary(string status)
    {
        var summary = Build([File("new/name.txt", status, 0, 0, null, previous: "old/name.txt")]);

        AssertSummary(summary, [new FileDiffStat("new/name.txt", 0, 0, false)], 0, 0,
            "diff --git a/old/name.txt b/new/name.txt\n--- a/old/name.txt\n+++ b/new/name.txt\n", truncated: false);
        Assert.Empty(PatchParser.AddedLines(summary.Patch));
    }

    [Fact]
    public void A_zero_byte_cap_still_reports_a_capped_file_list()
    {
        var summary = Build([File("a.txt", "modified", 1, 0, "@@ -0,0 +1 @@\n+x")], maxPatchBytes: 0, fileListCapped: true);

        Assert.Equal(string.Empty, summary.Patch);
        Assert.True(summary.PatchTruncated);
    }

    [Fact]
    public void A_path_with_a_line_break_cannot_forge_a_file_header()
    {
        var summary = Build([File("evil\n+++ b/other.txt", "added", 1, 0, "@@ -0,0 +1 @@\n+x")]);

        // Quoted and escaped the way git quotes unusual paths: the header stays on one line.
        Assert.Equal(
            "diff --git \"a/evil\\n+++ b/other.txt\" \"b/evil\\n+++ b/other.txt\"\n--- /dev/null\n+++ \"b/evil\\n+++ b/other.txt\"\n@@ -0,0 +1 @@\n+x\n",
            summary.Patch);
        Assert.Equal([new AddedLine("evil\\n+++ b/other.txt", 1, "x")], PatchParser.AddedLines(summary.Patch));
    }
}
