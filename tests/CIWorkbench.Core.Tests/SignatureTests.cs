using CIWorkbench.Core.Failures;

namespace CIWorkbench.Core.Tests;

public class SignatureTests
{
    private static string Norm(string log) => SignatureExtractor.Extract(log).Normalized ?? "<none>";

    [Fact] public void Exit_code_alone_is_unknown_never_a_signature()
    {
        var r = SignatureExtractor.Extract("##[error]Process completed with exit code 1.\n");
        Assert.Equal(SignatureKind.Unknown, r.Kind);
        Assert.Null(r.Normalized);
    }

    [Fact] public void Missing_log_is_unavailable_not_unknown() =>
        Assert.Equal(SignatureKind.LogUnavailable, SignatureExtractor.Extract(null).Kind);

    [Fact] public void Same_error_with_different_timestamps_shas_guids_and_workspaces_merges()
    {
        var a = "2026-10-01T10:00:01.1234567Z ##[error]Cannot find module 'x' in /home/runner/work/app/app/src (build 8f3a9c2d1e)\n2026-10-01T10:00:02Z ##[error]Process completed with exit code 1.";
        var b = "2026-10-09T23:59:59.9Z ##[error]Cannot find module 'x' in /home/runner/work/app/app/src (build 1b2c3d4e5f)\n2026-10-09T23:59:59Z ##[error]Process completed with exit code 1.";
        Assert.Equal(Norm(a), Norm(b));
        Assert.NotEqual("<none>", Norm(a));
    }

    [Theory]
    [InlineData("##[error]Failed in /home/runner/work/r/r/a.ts", "##[error]Failed in D:\\a\\r\\r\\a.ts")]
    [InlineData("##[error]Failed in /home/runner/work/r/r/a.ts", "##[error]Failed in /Users/runner/work/r/r/a.ts")]
    public void Runner_paths_normalize_across_os(string a, string b) => Assert.Equal(Norm(a), Norm(b));

    [Fact] public void Different_line_col_merges() =>
        Assert.Equal(Norm("Program.cs(12,5): error CS0246: type X not found"), Norm("Program.cs(99,41): error CS0246: type X not found"));

    [Fact] public void Msbuild_error_preferred_over_trailing_exit_code()
    {
        var log = "Build started\nProgram.cs(1,1): error CS0246: The type or namespace name 'Foo' could not be found\nProcess completed with exit code 1.";
        Assert.Contains("CS0246", Norm(log));
    }

    [Fact] public void Different_compiler_codes_do_not_merge() =>
        Assert.NotEqual(Norm("A.cs(1,1): error CS0246: nope"), Norm("A.cs(1,1): error CS1002: nope"));

    [Fact] public void Different_failed_test_names_do_not_merge() =>
        Assert.NotEqual(Norm("Failed LoginTests.Rejects_bad_password"), Norm("Failed CartTests.Totals_include_vat"));

    [Fact] public void Ansi_and_markers_are_stripped()
    {
        var s = SignatureExtractor.Extract("\u001b[31m##[error]\u001b[0mTimeoutError: page.goto timed out after 30000ms").Normalized!;
        Assert.False(s.Contains((char)27)); Assert.DoesNotContain("##[error]", s);
        Assert.StartsWith("TimeoutError", s);
    }

    [Fact] public void Error_codes_keep_their_case() =>
        Assert.Contains("NU1101", Norm("error NU1101: Unable to find package"));

    [Fact] public void Only_the_tail_of_a_huge_log_is_scanned()
    {
        var log = "AssertionError: ancient failure\n" + string.Concat(Enumerable.Repeat("noise line\n", 5000)) + "Error: recent failure\n";
        Assert.StartsWith("Error: recent failure", Norm(log));
    }

    [Fact] public void Signature_is_capped() =>
        Assert.True(Norm("Error: " + new string('x', 5000)).Length <= SignatureExtractor.MaxSignatureLength);
}

