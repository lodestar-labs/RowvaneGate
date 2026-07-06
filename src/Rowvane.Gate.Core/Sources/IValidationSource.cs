using Rowvane.Gate.Records;
using Rowvane.Gate.Rulesets;

namespace Rowvane.Gate.Sources;

/// <summary>
/// Streams root-entity records (with their full child subtrees) out of a source document,
/// guided by the ruleset's shape. Implementations must stream — memory is bounded by one
/// root subtree, never the file.
/// </summary>
public interface IValidationSource
{
    /// <summary>Format key: "csv", "xml", "json".</summary>
    string Format { get; }

    /// <summary>File extensions (without dot) this source accepts, for inference.</summary>
    IReadOnlyList<string> Extensions { get; }

    IAsyncEnumerable<ValidationRecord> ReadAsync(Stream stream, RulesetDocument ruleset, CancellationToken cancellationToken = default);
}

/// <summary>Raised when a document cannot be read at all (as opposed to record-level findings).</summary>
public sealed class SourceFormatException : Exception
{
    public SourceFormatException(string message) : base(message)
    {
    }

    public SourceFormatException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// Optional hook for analytical checks (the sql rule, profiling). Implemented by the
/// analytics package; the engine runs these checks only when a runner is registered.
/// </summary>
public interface IAnalyticsRunner
{
    /// <summary>
    /// Executes a sql check against the staged file and returns one message per violating
    /// row (already formatted from the row's columns).
    /// </summary>
    Task<IReadOnlyList<string>> RunSqlCheckAsync(
        string filePath,
        string format,
        RulesetDocument ruleset,
        SqlCheck check,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes several sql checks against the same staged file, returning one outcome per
    /// check, index-aligned. A check that fails reports its error in the outcome instead of
    /// throwing, so one bad query cannot stop the others. The default implementation calls
    /// <see cref="RunSqlCheckAsync"/> per check; implementations that pay a per-call
    /// materialization cost should override to stage the file once.
    /// </summary>
    async Task<IReadOnlyList<SqlCheckOutcome>> RunSqlChecksAsync(
        string filePath,
        string format,
        RulesetDocument ruleset,
        IReadOnlyList<SqlCheck> checks,
        CancellationToken cancellationToken = default)
    {
        var outcomes = new List<SqlCheckOutcome>(checks.Count);
        foreach (var check in checks)
        {
            try
            {
                outcomes.Add(new SqlCheckOutcome(
                    await RunSqlCheckAsync(filePath, format, ruleset, check, cancellationToken), null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcomes.Add(new SqlCheckOutcome(null, ex.Message));
            }
        }

        return outcomes;
    }
}

/// <summary>One sql check's result: its violation messages, or the error that stopped it.</summary>
public sealed record SqlCheckOutcome(IReadOnlyList<string>? Violations, string? Error);
