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
}
