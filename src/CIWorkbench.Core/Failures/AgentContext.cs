using System.Text;
using System.Text.RegularExpressions;
using CIWorkbench.Core.Models;

namespace CIWorkbench.Core.Failures;

/// <summary>Builds a bounded, secret-redacted text block for pasting into a coding agent. Nothing is sent anywhere.</summary>
public static partial class AgentContext
{
    public const int MaxExcerptChars = 4000;

    [GeneratedRegex(@"(github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|(?i:bearer)\s+[A-Za-z0-9._\-]{16,}|(?i:authorization:)\s*\S+|(?i:(?:password|secret|token|api[_-]?key)\s*[=:]\s*)\S+|AKIA[0-9A-Z]{16}|-----BEGIN [A-Z ]*PRIVATE KEY-----)")]
    private static partial Regex Secrets();

    public static string Redact(string text) => Secrets().Replace(text, "[REDACTED]");

    public static string Build(RepoRef repo, WorkflowRunInfo run, string? job, string? step, string? description, string? logTail)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# CI failure context");
        sb.AppendLine($"Repository: {repo.FullName}");
        sb.AppendLine($"Workflow: {run.WorkflowPath} ({run.Name})");
        sb.AppendLine($"Run: #{run.RunNumber} (id {run.Id}, attempt {run.Attempt}) {run.HtmlUrl}");
        sb.AppendLine($"Branch: {run.Branch}  Commit: {run.HeadSha}");
        if (job is not null) sb.AppendLine($"Failed job: {job}");
        if (step is not null) sb.AppendLine($"Failed step: {step}");
        if (description is not null) sb.AppendLine($"Failure: {Redact(description)}");
        if (!string.IsNullOrWhiteSpace(logTail))
        {
            var cleaned = string.Join('\n', logTail.Split('\n').Select(l => Redact(SignatureExtractor.StripDecorations(l))).Where(l => l.Length > 0));
            if (cleaned.Length > MaxExcerptChars) cleaned = "…" + cleaned[^MaxExcerptChars..];
            sb.AppendLine().AppendLine("Log excerpt (tail, redacted, bounded):").AppendLine("```").AppendLine(cleaned).AppendLine("```");
        }
        return sb.ToString();
    }
}

