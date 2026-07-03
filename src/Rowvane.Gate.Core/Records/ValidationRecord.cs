using Rowvane.Gate.Rulesets;

namespace Rowvane.Gate.Records;

/// <summary>
/// The canonical in-flight record every source reader produces: one entity instance with
/// its raw field values, its children, and a link to its parent. All values stay raw
/// strings — Gate validates what the file actually says, not a converted interpretation.
/// </summary>
public sealed class ValidationRecord
{
    public required EntityShape Entity { get; init; }

    public SourceLocation Location { get; init; }

    /// <summary>Raw source values keyed by field name (case-insensitive).</summary>
    public Dictionary<string, string?> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<ValidationRecord> Children { get; } = [];

    public ValidationRecord? Parent { get; set; }

    public string? Get(string field) => Fields.TryGetValue(field, out var value) ? value : null;

    public IEnumerable<ValidationRecord> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var descendant in child.SelfAndDescendants())
            {
                yield return descendant;
            }
        }
    }
}

/// <summary>Where a record came from: real parser line/column when available, plus a logical path.</summary>
public readonly record struct SourceLocation(long? Line, string? Path)
{
    public override string ToString() =>
        (Line, Path) switch
        {
            (not null, not null) => $"{Path} (line {Line})",
            (not null, null) => $"line {Line}",
            (null, not null) => Path!,
            _ => "unknown",
        };
}
