using Rowvane.Gate.Rulesets;

namespace Rowvane.Gate.Findings;

/// <summary>
/// One violation: which rule, where in the source (real line numbers), which field, what
/// the file actually contained, and why it failed. Structured so reports can be filtered,
/// aggregated, diffed, and rendered — never just a string.
/// </summary>
public sealed record Finding(
    string RuleId,
    Severity Severity,
    string Entity,
    string? Field,
    long? Line,
    string? Path,
    string? RawValue,
    string Message);

/// <summary>The result of validating one file against one ruleset.</summary>
public sealed class ValidationReport
{
    public required string Ruleset { get; init; }

    public required string Source { get; init; }

    public required string Format { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public double DurationMs { get; set; }

    public long RecordsRead { get; set; }

    /// <summary>Records per entity, for structural sanity at a glance.</summary>
    public Dictionary<string, long> RecordCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public long ErrorCount { get; set; }

    public long WarningCount { get; set; }

    public long InfoCount { get; set; }

    /// <summary>Total findings per rule id, including those truncated from <see cref="Findings"/>.</summary>
    public Dictionary<string, long> FindingsByRule { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Findings, truncated per rule at the ruleset's cap. Counts above are never truncated.</summary>
    public List<Finding> Findings { get; } = [];

    /// <summary>True when no error-severity findings were produced.</summary>
    public bool Valid => ErrorCount == 0;

    public void Add(Finding finding, int maxPerRule)
    {
        switch (finding.Severity)
        {
            case Severity.Error: ErrorCount++; break;
            case Severity.Warning: WarningCount++; break;
            default: InfoCount++; break;
        }

        var total = FindingsByRule.TryGetValue(finding.RuleId, out var count) ? count : 0;
        FindingsByRule[finding.RuleId] = total + 1;
        if (total < maxPerRule)
        {
            Findings.Add(finding);
        }
    }
}
