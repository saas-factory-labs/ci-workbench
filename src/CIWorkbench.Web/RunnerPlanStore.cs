using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CIWorkbench.Core.Runners;
using Microsoft.Extensions.Options;

namespace CIWorkbench.Web;

public enum PlanSource { None, Saved, Server }

/// <summary>
/// Allowances the user saved from Settings, kept on the server in one small JSON file (no database, nothing in the browser).
/// Entries are partitioned by a hash of the credential scope (data source, token fingerprint, allowlist), so a different token or
/// the demo never sees them; the hash also keeps repository names out of the file. Server configuration
/// (<c>RunnerInsights:Providers</c>) always wins over what is saved here. The demo scope lives in memory only.
/// </summary>
public sealed class RunnerPlanStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private sealed class FileModel { public int Version { get; set; } = 1; public Dictionary<string, Dictionary<string, ProviderPlanOptions>> Scopes { get; set; } = []; }

    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger<RunnerPlanStore>? _logger;
    private readonly Dictionary<string, Dictionary<string, ProviderPlanOptions>> _scopes = new(StringComparer.Ordinal);

    public RunnerPlanStore(IOptions<RunnerInsightsOptions> options, ILogger<RunnerPlanStore>? logger = null)
    {
        _logger = logger;
        var dir = options.Value.DataDirectory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            dir = Path.Combine(string.IsNullOrEmpty(local) ? Path.Combine(AppContext.BaseDirectory, "data") : local, "CIWorkbench");
        }
        _path = Path.Combine(dir, "runner-insights.json");
        Load();
    }

    public event Action? Changed;
    public string FilePath => _path;

    /// <summary>The demo's invented plans, so Demo mode shows every state. Editable in memory like any other scope; never written to disk.</summary>
    public void SeedDemo(string demoScope, IReadOnlyDictionary<string, ProviderPlanOptions> plans)
    {
        lock (_gate) if (!_scopes.ContainsKey(Key(demoScope))) _scopes[Key(demoScope)] = new Dictionary<string, ProviderPlanOptions>(plans, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, ProviderPlanOptions> Get(string scope)
    {
        lock (_gate) return _scopes.TryGetValue(Key(scope), out var d) ? new Dictionary<string, ProviderPlanOptions>(d, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, ProviderPlanOptions>(StringComparer.OrdinalIgnoreCase);
    }

    public bool Has(string scope, string planKey) { lock (_gate) return _scopes.TryGetValue(Key(scope), out var d) && d.ContainsKey(planKey); }

    public void Save(string scope, string planKey, ProviderPlanOptions plan)
    {
        lock (_gate)
        {
            if (!_scopes.TryGetValue(Key(scope), out var d)) _scopes[Key(scope)] = d = new Dictionary<string, ProviderPlanOptions>(StringComparer.OrdinalIgnoreCase);
            d[planKey] = plan;
            Persist(scope);
        }
        Changed?.Invoke();
    }

    public bool Reset(string scope, string planKey)
    {
        bool removed;
        lock (_gate) { removed = _scopes.TryGetValue(Key(scope), out var d) && d.Remove(planKey); if (removed) Persist(scope); }
        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>Opaque partition key: a hash, so neither tokens nor repository names are ever written.</summary>
    private static string Key(string scope) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..16];

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var m = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), Json);
            if (m?.Scopes is null) return;
            foreach (var (k, v) in m.Scopes) _scopes[k] = new Dictionary<string, ProviderPlanOptions>(v, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger?.LogWarning("Saved Runner Insights allowances could not be read ({Type}); starting without them.", ex.GetType().Name);   // never log the path or content
        }
    }

    // Caller holds the gate. The demo scope is never written.
    private void Persist(string scope)
    {
        if (scope == "demo") return;
        try
        {
            var model = new FileModel();
            foreach (var (k, v) in _scopes) if (k != Key("demo") && v.Count > 0) model.Scopes[k] = v;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(model, Json));
            File.Move(tmp, _path, overwrite: true);                       // atomic replace: a crash never leaves half a file
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning("Runner Insights allowances could not be saved ({Type}). They stay in memory until restart.", ex.GetType().Name);
        }
    }
}
