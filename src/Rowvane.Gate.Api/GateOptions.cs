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
}
