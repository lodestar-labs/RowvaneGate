namespace Rowvane.Gate.Api;

public sealed class GateOptions
{
    public const string SectionName = "Gate";

    /// <summary>Directory where registered rulesets persist as JSON.</summary>
    public string RulesetDirectory { get; set; } = "data/rulesets";

    /// <summary>Directory where uploads are staged during validation.</summary>
    public string StagingPath { get; set; } = "data/staging";

    /// <summary>Finite upload ceiling — the endpoint is unauthenticated in v1.</summary>
    public long MaxUploadBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Keep staged files after validation (useful when debugging sql rules).</summary>
    public bool KeepStagedFiles { get; set; }

    /// <summary>
    /// Staged files older than this are swept even when something went wrong with inline
    /// cleanup (crash, eviction) or KeepStagedFiles is on — an unattended host must not
    /// fill its disk with orphaned uploads.
    /// </summary>
    public TimeSpan StagedFileRetention { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Allow SQL rules to access the filesystem directly (the {file} placeholder, custom
    /// read options). Off by default: sandboxed rules can only query the staged 'data'
    /// table, so a ruleset can never read or write other files on the host.
    /// </summary>
    public bool AllowUnsandboxedSqlRules { get; set; }

    /// <summary>
    /// When set, every /api request must carry this value in the X-Api-Key header. Leave
    /// null only behind an authenticating gateway.
    /// </summary>
    public string? ApiKey { get; set; }
}
