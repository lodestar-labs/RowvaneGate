using System.Collections.Concurrent;
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

    private string PathFor(string name)
    {
        var safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return Path.Combine(directory, $"{safe}.json");
    }
}
