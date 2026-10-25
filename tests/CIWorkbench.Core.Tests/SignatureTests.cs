using CIWorkbench.Core.Failures;
using System.Threading.Tasks;

namespace CIWorkbench.Core.Tests;

public class SignatureTests
{
    private static string Norm(string log) => SignatureExtractor.Extract(log).Normalized ?? "<none>";

    [Test] public async Task Exit_code_alone_is_unknown_never_a_signature()
    {
        var r = SignatureExtractor.Extract("##[error]Process completed with exit code 1.\n");
        await Assert.That(r.Kind).IsEqualTo(SignatureKind.Unknown);
        await Assert.That(r.Normalized).IsNull();
    }

    [Test] public async Task Missing_log_is_unavailable_not_unknown() =>
        await Assert.That(SignatureExtractor.Extract(null).Kind).IsEqualTo(SignatureKind.LogUnavailable);

    [Test] public async Task Same_error_with_different_timestamps_shas_guids_and_workspaces_merges()
    {
        var a = "2026-10-01T10:00:01.1234567Z ##[error]Cannot find module 'x' in /home/runner/work/app/app/src (build 8f3a9c2d1e)\n2026-10-01T10:00:02Z ##[error]Process completed with exit code 1.";
        var b = "2026-10-09T23:59:59.9Z ##[error]Cannot find module 'x' in /home/runner/work/app/app/src (build 1b2c3d4e5f)\n2026-10-09T23:59:59Z ##[error]Process completed with exit code 1.";
        await Assert.That(Norm(b)).IsEqualTo(Norm(a));
        await Assert.That(Norm(a)).IsNotEqualTo("<none>");
    }

    [Test]
    [Arguments("##[error]Failed in /home/runner/work/r/r/a.ts", "##[error]Failed in D:\\a\\r\\r\\a.ts")]
    [Arguments("##[error]Failed in /home/runner/work/r/r/a.ts", "##[error]Failed in /Users/runner/work/r/r/a.ts")]
    public async Task Runner_paths_normalize_across_os(string a, string b) => await Assert.That(Norm(b)).IsEqualTo(Norm(a));

    [Test] public async Task Different_line_col_merges() =>
        await Assert.That(Norm("Program.cs(99,41): error CS0246: type X not found")).IsEqualTo(Norm("Program.cs(12,5): error CS0246: type X not found"));

    [Test] public async Task Msbuild_error_preferred_over_trailing_exit_code()
    {
        var log = "Build started\nProgram.cs(1,1): error CS0246: The type or namespace name 'Foo' could not be found\nProcess completed with exit code 1.";
        await Assert.That(Norm(log)).Contains("CS0246");
    }

    [Test] public async Task Different_compiler_codes_do_not_merge() =>
        await Assert.That(Norm("A.cs(1,1): error CS1002: nope")).IsNotEqualTo(Norm("A.cs(1,1): error CS0246: nope"));

    [Test] public async Task Different_failed_test_names_do_not_merge() =>
        await Assert.That(Norm("Failed CartTests.Totals_include_vat")).IsNotEqualTo(Norm("Failed LoginTests.Rejects_bad_password"));

    [Test] public async Task Ansi_and_markers_are_stripped()
    {
        var s = SignatureExtractor.Extract("\u001b[31m##[error]\u001b[0mTimeoutError: page.goto timed out after 30000ms").Normalized!;
        await Assert.That(s.Contains((char)27)).IsFalse(); await Assert.That(s).DoesNotContain("##[error]");
        await Assert.That(s).StartsWith("TimeoutError");
    }

    [Test] public async Task Error_codes_keep_their_case() =>
        await Assert.That(Norm("error NU1101: Unable to find package")).Contains("NU1101");

    [Test] public async Task Only_the_tail_of_a_huge_log_is_scanned()
    {
        var log = "AssertionError: ancient failure\n" + string.Concat(Enumerable.Repeat("noise line\n", 5000)) + "Error: recent failure\n";
        await Assert.That(Norm(log)).StartsWith("Error: recent failure");
    }

    [Test] public async Task Signature_is_capped() =>
        await Assert.That(Norm("Error: " + new string('x', 5000)).Length <= SignatureExtractor.MaxSignatureLength).IsTrue();
}
