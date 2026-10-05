using CodebaseGuardian.Checks;

namespace CodebaseGuardian.Tests.App;

public class TestOutputParserTests
{
    [Fact]
    public void Vstest_output_yields_failed_names_and_the_summary_line()
    {
        const string output = """
            Starting test execution, please wait...
              Passed Demo.Tests.Math.Adds [3 ms]
              Failed Demo.Tests.Math.Subtracts [12 ms]
              Error Message:
               Assert.Equal() Failure
              Failed Demo.Tests.Math.Divides(a: 1, b: 0) [< 1 ms]
            Failed!  - Failed:     2, Passed:     1, Skipped:     0, Total:     3, Duration: 20 ms - Demo.Tests.dll (net10.0)
            """;

        var parsed = TestOutputParser.Parse(output, 1);

        Assert.Equal(["Demo.Tests.Math.Subtracts", "Demo.Tests.Math.Divides(a: 1, b: 0)"], parsed.FailedTests);
        Assert.StartsWith("Failed!  - Failed:     2", parsed.Summary);
    }

    [Fact]
    public void Microsoft_testing_platform_output_yields_failed_names_and_the_last_summary_line()
    {
        const string output = """
            failed Demo.Tests.Math.Subtracts (12ms)
              Assert.Equal() Failure
            failed Demo.Tests.Math.Subtracts (12ms)
            Test run summary: Failed!
              total: 3
              failed: 1
            """;

        var parsed = TestOutputParser.Parse(output, 2);

        Assert.Equal(["Demo.Tests.Math.Subtracts"], parsed.FailedTests);
        Assert.Equal("failed: 1", parsed.Summary);
    }

    [Fact]
    public void Passing_run_has_no_failed_tests()
    {
        var parsed = TestOutputParser.Parse("Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 9 ms\n", 0);

        Assert.Empty(parsed.FailedTests);
        Assert.StartsWith("Passed!", parsed.Summary);
    }

    [Fact]
    public void Npm_output_summarises_with_the_last_npm_err_line()
    {
        const string output = """
            > demo@1.0.0 test
            > jest
            npm ERR! Test failed.  See above for more details.
            """;

        var parsed = TestOutputParser.Parse(output, 1);

        Assert.Empty(parsed.FailedTests);
        Assert.Equal("npm ERR! Test failed.  See above for more details.", parsed.Summary);
    }

    [Fact]
    public void Unrecognised_output_falls_back_to_the_exit_code()
    {
        var parsed = TestOutputParser.Parse("something exploded", 137);

        Assert.Equal("exit code 137", parsed.Summary);
        Assert.Empty(parsed.FailedTests);
    }

    [Fact]
    public void At_most_50_distinct_failed_tests_are_returned()
    {
        var output = string.Join('\n', Enumerable.Range(0, 80).Select(i => $"  Failed T.N{i} [1 ms]"));

        Assert.Equal(50, TestOutputParser.Parse(output, 1).FailedTests.Count);
    }
}
