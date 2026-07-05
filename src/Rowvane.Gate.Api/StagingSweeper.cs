using Microsoft.Extensions.Options;

namespace Rowvane.Gate.Api;

/// <summary>
/// Deletes staged upload files older than the configured retention. Inline cleanup
/// handles the happy path; this sweeper covers everything else — crashes between staging
/// and cleanup, KeepStagedFiles left on, container evictions — so the staging directory
/// can never grow without bound on a long-running host.
/// </summary>
public sealed class StagingSweeper(IOptions<GateOptions> options, ILogger<StagingSweeper> logger)
    : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            SweepOnce();
            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void SweepOnce()
    {
        var path = options.Value.StagingPath;
        if (!Directory.Exists(path))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - options.Value.StagedFileRetention;
        var swept = 0;
        foreach (var file in Directory.EnumerateFiles(path))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    swept++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use or locked — the next sweep gets it.
                logger.LogDebug(ex, "Could not sweep staged file {File}", file);
            }
        }

        if (swept > 0)
        {
            logger.LogInformation("Swept {Count} staged files older than {Retention}", swept, options.Value.StagedFileRetention);
        }
    }
}
