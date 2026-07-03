using System.Text.Json.Serialization;

namespace Rowvane.Gate.Rulesets;

public enum Severity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// One rule: a target (entity, optionally a field), a check, a severity, and an optional
/// applicability condition. The check itself is polymorphic — see <see cref="RuleCheck"/>.
/// </summary>
public sealed class Rule
{
    /// <summary>Stable identifier used in findings and suppressions. Auto-assigned (R001…) when omitted.</summary>
    public string? Id { get; set; }

    /// <summary>Target entity. Null means the rule targets the dataset as a whole.</summary>
    public string? Entity { get; set; }

    /// <summary>Target field, for field-level checks.</summary>
    public string? Field { get; set; }

    public Severity Severity { get; set; } = Severity.Error;

    /// <summary>Overrides the check's generated message.</summary>
    public string? Message { get; set; }

    /// <summary>The rule only applies when this condition holds for the record.</summary>
    public RuleCondition? When { get; set; }

    public required RuleCheck Check { get; set; }
}

/// <summary>Applicability condition: "when {field} is one of {values}".</summary>
public sealed class RuleCondition
{
    public required string Field { get; set; }

    public List<string> OneOf { get; set; } = [];

    public bool Negate { get; set; }
}

/// <summary>
/// Base of all checks. Polymorphic JSON via a "type" discriminator, so rulesets read
/// naturally: <c>{ "type": "range", "min": 0, "max": 100 }</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(RequiredCheck), "required")]
[JsonDerivedType(typeof(TypeCheck), "dataType")]
[JsonDerivedType(typeof(RangeCheck), "range")]
[JsonDerivedType(typeof(LengthCheck), "length")]
[JsonDerivedType(typeof(RegexCheck), "regex")]
[JsonDerivedType(typeof(InListCheck), "inList")]
[JsonDerivedType(typeof(CompareCheck), "compare")]
[JsonDerivedType(typeof(UniqueCheck), "unique")]
[JsonDerivedType(typeof(AggregateCheck), "aggregate")]
[JsonDerivedType(typeof(RowCountCheck), "rowCount")]
[JsonDerivedType(typeof(SqlCheck), "sql")]
public abstract class RuleCheck
{
    /// <summary>Check-specific structural validation; yield one message per problem.</summary>
    public virtual IEnumerable<string> Validate(RulesetDocument document, EntityShape? entity, Rule rule)
    {
        if (RequiresField && string.IsNullOrWhiteSpace(rule.Field))
        {
            yield return $"check '{GetType().Name}' requires a 'field'.";
        }

        if (RequiresField && entity is not null && rule.Field is not null && entity.FindField(rule.Field) is null)
        {
            yield return $"field '{rule.Field}' does not exist on entity '{entity.Name}'.";
        }
    }

    [JsonIgnore]
    public virtual bool RequiresField => true;
}

/// <summary>Value must be present and non-blank.</summary>
public sealed class RequiredCheck : RuleCheck;

public enum DataKind
{
    String,
    Integer,
    Decimal,
    Boolean,
    Date,
    DateTime,
    Time,
    Guid,
}

/// <summary>Value must parse as the given kind (invariant culture; optional exact format).</summary>
public sealed class TypeCheck : RuleCheck
{
    public DataKind Kind { get; set; } = DataKind.String;

    /// <summary>Exact format for date/time kinds, e.g. "yyyyMMdd".</summary>
    public string? Format { get; set; }
}

/// <summary>Numeric or date range. Bounds are inclusive unless marked exclusive.</summary>
public sealed class RangeCheck : RuleCheck
{
    public decimal? Min { get; set; }

    public decimal? Max { get; set; }

    public bool ExclusiveMin { get; set; }

    public bool ExclusiveMax { get; set; }

    public override IEnumerable<string> Validate(RulesetDocument document, EntityShape? entity, Rule rule)
    {
        foreach (var problem in base.Validate(document, entity, rule))
        {
            yield return problem;
        }

        if (Min is null && Max is null)
        {
            yield return "range check requires 'min' and/or 'max'.";
        }
    }
}

/// <summary>String length bounds.</summary>
public sealed class LengthCheck : RuleCheck
{
    public int? Min { get; set; }

    public int? Max { get; set; }
}

/// <summary>Value must match (or not match) a regular expression.</summary>
public sealed class RegexCheck : RuleCheck
{
    public required string Pattern { get; set; }

    public bool Negate { get; set; }
}

/// <summary>Value must be one of (or none of) a set of allowed values.</summary>
public sealed class InListCheck : RuleCheck
{
    public List<string> Values { get; set; } = [];

    public bool CaseInsensitive { get; set; } = true;

    public bool Negate { get; set; }
}

public enum CompareOp
{
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
}

/// <summary>
/// Compares the rule's field against a literal value, another field on the same record,
/// or a field on an ancestor record (<c>"otherField": "parent.VSsequenceNumber"</c> —
/// <c>parent.</c> may be repeated to climb further).
/// </summary>
public sealed class CompareCheck : RuleCheck
{
    public CompareOp Op { get; set; } = CompareOp.Eq;

    /// <summary>Literal right-hand value.</summary>
    public string? Value { get; set; }

    /// <summary>Right-hand field path: "OtherField" or "parent.Field" (repeatable prefix).</summary>
    public string? OtherField { get; set; }

    /// <summary>Compare numerically when both sides parse as numbers (default true).</summary>
    public bool Numeric { get; set; } = true;

    public override IEnumerable<string> Validate(RulesetDocument document, EntityShape? entity, Rule rule)
    {
        foreach (var problem in base.Validate(document, entity, rule))
        {
            yield return problem;
        }

        if (Value is null && OtherField is null)
        {
            yield return "compare check requires 'value' or 'otherField'.";
        }
    }
}

public enum UniqueScope
{
    /// <summary>Unique across the whole file.</summary>
    Dataset,

    /// <summary>Unique among siblings under the same parent record.</summary>
    Parent,
}

/// <summary>Composite-key uniqueness over one entity's records.</summary>
public sealed class UniqueCheck : RuleCheck
{
    public List<string> Fields { get; set; } = [];

    public UniqueScope Scope { get; set; } = UniqueScope.Dataset;

    [JsonIgnore]
    public override bool RequiresField => false;

    public override IEnumerable<string> Validate(RulesetDocument document, EntityShape? entity, Rule rule)
    {
        if (entity is null)
        {
            yield return "unique check requires an 'entity'.";
            yield break;
        }

        if (Fields.Count == 0)
        {
            yield return "unique check requires at least one field in 'fields'.";
        }

        foreach (var field in Fields.Where(f => entity.FindField(f) is null))
        {
            yield return $"unique field '{field}' does not exist on entity '{entity.Name}'.";
        }
    }
}

public enum AggregateFunction
{
    Sum,
    Count,
    Avg,
    Min,
    Max,
}

/// <summary>
/// Aggregates a child entity's field per parent record and compares it against the
/// parent's field — exactly (op) or within a percentage deviation.
/// </summary>
public sealed class AggregateCheck : RuleCheck
{
    public required string ChildEntity { get; set; }

    /// <summary>Child field to aggregate. Ignored for Count.</summary>
    public string? ChildField { get; set; }

    public AggregateFunction Function { get; set; } = AggregateFunction.Sum;

    public CompareOp Op { get; set; } = CompareOp.Eq;

    /// <summary>Allowed deviation in percent; when set, Op is ignored and |Δ| ≤ pct applies.</summary>
    public decimal? DeviationPercent { get; set; }

    public override IEnumerable<string> Validate(RulesetDocument document, EntityShape? entity, Rule rule)
    {
        foreach (var problem in base.Validate(document, entity, rule))
        {
            yield return problem;
        }

        if (entity is null)
        {
            yield break;
        }

        var child = entity.FindChild(ChildEntity);
        if (child is null)
        {
            yield return $"'{ChildEntity}' is not a child of entity '{entity.Name}'.";
        }
        else if (Function != AggregateFunction.Count)
        {
            if (ChildField is null)
            {
                yield return $"aggregate '{Function}' requires 'childField'.";
            }
            else if (child.FindField(ChildField) is null)
            {
                yield return $"child field '{ChildField}' does not exist on '{ChildEntity}'.";
            }
        }
    }
}

/// <summary>Bounds on how many records of an entity the file may contain.</summary>
public sealed class RowCountCheck : RuleCheck
{
    public long? Min { get; set; }

    public long? Max { get; set; }

    [JsonIgnore]
    public override bool RequiresField => false;

    public override IEnumerable<string> Validate(RulesetDocument document, EntityShape? entity, Rule rule)
    {
        if (entity is null)
        {
            yield return "rowCount check requires an 'entity'.";
        }

        if (Min is null && Max is null)
        {
            yield return "rowCount check requires 'min' and/or 'max'.";
        }
    }
}

/// <summary>
/// Analytical rule: a SQL query executed by the analytics engine over the staged file.
/// Every returned row is a finding; expose the columns you want in the message via
/// SELECT. Requires the analytics package to be enabled.
/// </summary>
public sealed class SqlCheck : RuleCheck
{
    public required string Query { get; set; }

    [JsonIgnore]
    public override bool RequiresField => false;

    public override IEnumerable<string> Validate(RulesetDocument document, EntityShape? entity, Rule rule)
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            yield return "sql check requires a 'query'.";
        }
    }
}
