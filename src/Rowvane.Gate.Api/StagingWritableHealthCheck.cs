using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Rowvane.Gate.Api;

/// <summary>
/// Readiness: validation stages every upload to disk first, so a host whose staging
/// directory cannot be written (full disk, missing mount, permissions) must not receive
/// traffic.
/// </summary>
public sealed class StagingWritableHealthCheck(IOptions<GateOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(options.Value.StagingPath);
            var probe = Path.Combine(options.Value.StagingPath, $".health_{Guid.NewGuid():n}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return Task.FromResult(HealthCheckResult.Healthy("Staging directory writable."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Staging directory not writable.", ex));
        }
    }
}
