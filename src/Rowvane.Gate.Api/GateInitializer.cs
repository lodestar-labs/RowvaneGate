using Rowvane.Gate.Engine;

namespace Rowvane.Gate.Api;

/// <summary>Loads persisted rulesets into the registry at startup.</summary>
public sealed class GateInitializer(
    IRulesetRegistry registry,
    RulesetDirectoryStore store,
    ILogger<GateInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var ruleset in await store.LoadAllAsync(cancellationToken))
        {
            registry.Register(ruleset);
        }

        logger.LogInformation(
            "Rowvane Gate ready: {Count} rulesets registered ({Names})",
            registry.All.Count,
            string.Join(", ", registry.All.Select(r => r.Name)));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
