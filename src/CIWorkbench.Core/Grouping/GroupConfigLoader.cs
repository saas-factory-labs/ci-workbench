using CIWorkbench.Core.Models;

namespace CIWorkbench.Core.Grouping;

/// <param name="Result">Parsed configuration; an empty configuration when no file exists.</param>
/// <param name="SourcePath">Repository path the configuration was read from, or null when no configuration file exists.</param>
public sealed record GroupConfigLoadResult(GroupConfigParseResult Result, string? SourcePath);

/// <summary>
/// Finds the central configuration of one repository: <c>.github/ciworkbench.yml</c> first, then the legacy root <c>.ciworkbench.yml</c>.
/// The first file that exists wins, even when it is invalid (an invalid preferred file never silently falls back to the legacy one).
/// Only the paths below are requested, and only for the repository it is given.
/// </summary>
public static class GroupConfigLoader
{
    public static async Task<GroupConfigLoadResult> LoadAsync(ICiProvider provider, RepoRef repo, CancellationToken ct)
    {
        foreach (var path in GroupConfigContract.SearchPaths)
        {
            string? text;
            try { text = await provider.GetFileAsync(repo, path, ct); }
            catch (CiProviderException ex)
            {
                var diag = new ConfigDiagnostic(ConfigSeverity.Warning, "unreadable", $"could not be read ({ex.Message}); using workflow comments and defaults");
                return new(new GroupConfigParseResult(new GroupConfig(), [diag]), path);
            }
            if (text is not null) return new(GroupConfigParser.Parse(text), path);
        }
        return new(GroupConfigParser.Parse(null), null);
    }
}
