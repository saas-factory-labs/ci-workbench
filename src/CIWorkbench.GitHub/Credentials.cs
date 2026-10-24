namespace CIWorkbench.GitHub;

public enum CredentialKind { PersonalAccessToken, AppInstallation, UserToServer }

/// <summary>Where a request is going. A future GitHub App source needs <see cref="Owner"/> to pick an installation.</summary>
public sealed record CiScope(string? Owner = null, string? Repo = null);

public sealed record GitHubCredential(string Token, CredentialKind Kind, DateTimeOffset? ExpiresAt = null)
{
    public override string ToString() => $"GitHubCredential {{ Kind = {Kind}, Token = ***** }}"; // never log tokens
}

/// <summary>"Who am I acting as." Resolved per request so tokens are never cached inside an HttpClient.</summary>
public interface IGitHubCredentialSource
{
    ValueTask<GitHubCredential?> GetAsync(CiScope scope, CancellationToken ct);
    /// <summary>Stable, non-secret identity used to partition caches (e.g. "pat" or "installation:123").</summary>
    string Identity { get; }
}

public sealed class GitHubOptions
{
    public const string Section = "GitHub";
    /// <summary>Fine-grained, read-only PAT. Supply via user-secrets or the GitHub__Token environment variable; never commit.</summary>
    public string? Token { get; set; }
    public string ApiBaseUrl { get; set; } = "https://api.github.com/";
    /// <summary>Optional allowlist of "owner/name" or "owner/*". Empty = everything the token can see.</summary>
    public List<string> AllowedRepositories { get; set; } = [];
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int MaxRateLimitWaitSeconds { get; set; } = 30;
    public bool HasToken => !string.IsNullOrWhiteSpace(Token);
}

public sealed class PatCredentialSource(Microsoft.Extensions.Options.IOptions<GitHubOptions> options) : IGitHubCredentialSource
{
    public string Identity => "pat";
    public ValueTask<GitHubCredential?> GetAsync(CiScope scope, CancellationToken ct) =>
        ValueTask.FromResult(options.Value.HasToken ? new GitHubCredential(options.Value.Token!, CredentialKind.PersonalAccessToken) : null);
}

