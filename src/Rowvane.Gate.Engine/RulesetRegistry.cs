using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Rowvane.Gate.Rulesets;

namespace Rowvane.Gate.Engine;

/// <summary>Live catalog of registered rulesets. Thread-safe; updated at runtime through the API.</summary>
public interface IRulesetRegistry
{
    void Register(RulesetDocument ruleset);

    bool Remove(string name);

    RulesetDocument? Find(string name);

    IReadOnlyList<RulesetDocument> All { get; }

    RulesetDocument GetRequired(string name) =>
        Find(name) ?? throw new KeyNotFoundException($"Ruleset '{name}' is not registered.");
}

public sealed class RulesetRegistry : IRulesetRegistry
{
    private readonly ConcurrentDictionary<string, RulesetDocument> _rulesets = new(StringComparer.OrdinalIgnoreCase);

    public void Register(RulesetDocument ruleset)
    {
        var errors = ruleset.Validate();
        if (errors.Count > 0)
        {
            throw new RulesetException(
                $"Ruleset '{ruleset.Name}' is invalid:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", errors)}");
        }

        ruleset.AssignRuleIds();
        _rulesets[ruleset.Name] = ruleset;
    }

    public bool Remove(string name) => _rulesets.TryRemove(name, out _);

    public RulesetDocument? Find(string name) =>
        _rulesets.TryGetValue(name, out var ruleset) ? ruleset : null;

    public IReadOnlyList<RulesetDocument> All => [.. _rulesets.Values.OrderBy(r => r.Name)];
}

/// <summary>
/// File-based ruleset persistence: one JSON document per ruleset in a directory, so
/// rulesets can be versioned in git and deployed with the app.
/// </summary>
public sealed class RulesetDirectoryStore(string directory, ILogger<RulesetDirectoryStore> logger)
{
    public async Task<IReadOnlyList<RulesetDocument>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var rulesets = new List<RulesetDocument>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(file);
                rulesets.Add(await RulesetSerializer.DeserializeAsync(stream, cancellationToken));
            }
            catch (RulesetException ex)
            {
                logger.LogError(ex, "Skipping invalid ruleset {RulesetFile}", file);
            }
        }

        return rulesets;
    }

    public async Task SaveAsync(RulesetDocument ruleset, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(PathFor(ruleset.Name), RulesetSerializer.Serialize(ruleset), cancellationToken);
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = PathFor(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <summary>Last write time of the persisted document, or null when none exists.</summary>
    public DateTime? LastWriteUtc(string name)
    {
        var path = PathFor(name);
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }

    public async Task<RulesetDocument?> LoadAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await RulesetSerializer.DeserializeAsync(stream, cancellationToken);
        }
        catch (RulesetException ex)
        {
            logger.LogError(ex, "Persisted ruleset {RulesetFile} is invalid", path);
            return null;
        }
    }

    public string PathFor(string name)
    {
        var safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        if (!string.Equals(safe, name, StringComparison.Ordinal))
        {
            // Distinct names that sanitize identically ("a/b" and "a.b" both become "a_b")
            // must not share a file, or one silently overwrites the other on disk.
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToLowerInvariant())))[..8];
            safe = $"{safe}_{hash}";
        }

        return Path.Combine(directory, $"{safe}.json");
    }
}

/// <summary>
/// Registry that reads through to the directory store, so several replicas sharing a
/// ruleset volume stay in sync without restarts: a ruleset registered (or updated) on one
/// replica is picked up by the others the next time it is asked for, and one deleted
/// elsewhere stops resolving here. Cheap — one file-timestamp probe per lookup.
/// </summary>
public sealed class StoreBackedRulesetRegistry(RulesetDirectoryStore store, ILogger<StoreBackedRulesetRegistry> logger)
    : IRulesetRegistry
{
    private readonly RulesetRegistry _inner = new();
    private readonly ConcurrentDictionary<string, DateTime> _loadedStamps = new(StringComparer.OrdinalIgnoreCase);

    public void Register(RulesetDocument ruleset)
    {
        _inner.Register(ruleset);
        _loadedStamps[ruleset.Name] = store.LastWriteUtc(ruleset.Name) ?? DateTime.UtcNow;
    }

    public bool Remove(string name)
    {
        _loadedStamps.TryRemove(name, out _);
        return _inner.Remove(name);
    }

    public RulesetDocument? Find(string name)
    {
        var stamp = store.LastWriteUtc(name);
        var cached = _inner.Find(name);
        if (stamp is null)
        {
            // Never persisted (code-registered) rulesets stay; ones we loaded from a file
            // that has since disappeared were deleted by another replica.
            if (cached is not null && _loadedStamps.ContainsKey(name))
            {
                Remove(name);
                return null;
            }

            return cached;
        }

        if (cached is not null && _loadedStamps.TryGetValue(name, out var loaded) && loaded >= stamp)
        {
            return cached;
        }

        var fresh = store.LoadAsync(name).GetAwaiter().GetResult();
        if (fresh is null)
        {
            return cached;
        }

        try
        {
            Register(fresh);
            logger.LogInformation("Reloaded ruleset {Ruleset} from the shared store", name);
            return _inner.Find(name);
        }
        catch (RulesetException ex)
        {
            logger.LogError(ex, "Ruleset {Ruleset} changed on disk but is invalid; keeping the cached version", name);
            return cached;
        }
    }

    public IReadOnlyList<RulesetDocument> All => _inner.All;
}
