using System.Globalization;
using System.Text.RegularExpressions;
using Rowvane.Gate.Findings;
using Rowvane.Gate.Records;
using Rowvane.Gate.Rulesets;

namespace Rowvane.Gate.Engine;

/// <summary>
/// A rule prepared for the hot path: config parsed once (regexes compiled, lists frozen
/// into sets, numbers pre-parsed), evaluation reduced to a single delegate call per
/// record. No reflection and no re-interpretation of the ruleset during streaming.
/// </summary>
internal sealed class CompiledRule
{
    private readonly Rule _rule;
    private readonly Func<ValidationRecord, string?> _evaluate;
    private readonly Func<ValidationRecord, bool>? _when;

    private CompiledRule(Rule rule, Func<ValidationRecord, string?> evaluate, Func<ValidationRecord, bool>? when)
    {
        _rule = rule;
        _evaluate = evaluate;
        _when = when;
    }

    public Rule Rule => _rule;

    /// <summary>Returns a finding for the record, or null when the rule passes / doesn't apply.</summary>
    public Finding? Evaluate(ValidationRecord record)
    {
        if (_when is not null && !_when(record))
        {
            return null;
        }

        var message = _evaluate(record);
        if (message is null)
        {
            return null;
        }

        return new Finding(
            _rule.Id!,
            _rule.Severity,
            record.Entity.Name,
            _rule.Field,
            record.Location.Line,
            record.Location.Path,
            _rule.Field is null ? null : record.Get(_rule.Field),
            _rule.Message ?? message);
    }

    /// <summary>Builds the evaluator for every per-record check kind. Dataset-scoped checks are handled by accumulators.</summary>
    public static CompiledRule? TryCompile(Rule rule)
    {
        Func<ValidationRecord, string?>? evaluate = rule.Check switch
        {
            RequiredCheck => record =>
                string.IsNullOrWhiteSpace(record.Get(rule.Field!))
                    ? "Required value is missing."
                    : null,

            TypeCheck check => CompileType(rule.Field!, check),
            RangeCheck check => CompileRange(rule.Field!, check),
            LengthCheck check => CompileLength(rule.Field!, check),
            RegexCheck check => CompileRegex(rule.Field!, check),
            InListCheck check => CompileInList(rule.Field!, check),
            CompareCheck check => CompileCompare(rule.Field!, check),
            _ => null,
        };

        if (evaluate is null)
        {
            return null;
        }

        return new CompiledRule(rule, evaluate, CompileCondition(rule.When));
    }

    internal static Func<ValidationRecord, bool>? CompileCondition(RuleCondition? condition)
    {
        if (condition is null)
        {
            return null;
        }

        var values = condition.OneOf.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return record =>
        {
            var value = record.Get(condition.Field);
            var matches = value is not null && values.Contains(value.Trim());
            return condition.Negate ? !matches : matches;
        };
    }

    private static Func<ValidationRecord, string?> CompileType(string field, TypeCheck check)
    {
        Func<string, bool> parses = check.Kind switch
        {
            DataKind.Integer => s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            DataKind.Decimal => s => decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
            DataKind.Boolean => s => s is "true" or "false" or "1" or "0" or "True" or "False" or "TRUE" or "FALSE",
            DataKind.Guid => s => Guid.TryParse(s, out _),
            DataKind.Date when check.Format is { } format =>
                s => DateOnly.TryParseExact(s, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            DataKind.Date => s => DateOnly.TryParse(s, CultureInfo.InvariantCulture, out _),
            DataKind.DateTime when check.Format is { } format =>
                s => DateTime.TryParseExact(s, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            DataKind.DateTime => s => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            DataKind.Time when check.Format is { } format =>
                s => TimeOnly.TryParseExact(s, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            DataKind.Time => s => TimeOnly.TryParse(s, CultureInfo.InvariantCulture, out _),
            _ => _ => true,
        };

        var expected = check.Format is null ? check.Kind.ToString() : $"{check.Kind} ({check.Format})";
        return record =>
        {
            var value = record.Get(field);
            return value is null || parses(value.Trim()) ? null : $"Value is not a valid {expected}.";
        };
    }

    private static Func<ValidationRecord, string?> CompileRange(string field, RangeCheck check)
    {
        return record =>
        {
            var raw = record.Get(field);
            if (raw is null)
            {
                return null;
            }

            if (!decimal.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return "Value is not numeric, so the range cannot be checked.";
            }

            if (check.Min is { } min && (check.ExclusiveMin ? value <= min : value < min))
            {
                return $"Value {value} is below the minimum {(check.ExclusiveMin ? "(exclusive) " : string.Empty)}{min}.";
            }

            if (check.Max is { } max && (check.ExclusiveMax ? value >= max : value > max))
            {
                return $"Value {value} is above the maximum {(check.ExclusiveMax ? "(exclusive) " : string.Empty)}{max}.";
            }

            return null;
        };
    }

    private static Func<ValidationRecord, string?> CompileLength(string field, LengthCheck check)
    {
        return record =>
        {
            var value = record.Get(field);
            if (value is null)
            {
                return null;
            }

            if (check.Min is { } min && value.Length < min)
            {
                return $"Value is {value.Length} characters; the minimum is {min}.";
            }

            if (check.Max is { } max && value.Length > max)
            {
                return $"Value is {value.Length} characters; the maximum is {max}.";
            }

            return null;
        };
    }

    private static Func<ValidationRecord, string?> CompileRegex(string field, RegexCheck check)
    {
        var regex = new Regex(check.Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        return record =>
        {
            var value = record.Get(field);
            if (value is null)
            {
                return null;
            }

            bool matches;
            try
            {
                matches = regex.IsMatch(value);
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological pattern/value combination must produce a finding on this
                // record, not abort the whole run.
                return $"Pattern '{check.Pattern}' timed out evaluating this value; the value was not validated.";
            }

            if (check.Negate ? matches : !matches)
            {
                return check.Negate
                    ? $"Value matches the forbidden pattern '{check.Pattern}'."
                    : $"Value does not match the pattern '{check.Pattern}'.";
            }

            return null;
        };
    }

    private static Func<ValidationRecord, string?> CompileInList(string field, InListCheck check)
    {
        var comparer = check.CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var values = check.Values.ToHashSet(comparer);
        return record =>
        {
            var value = record.Get(field);
            if (value is null)
            {
                return null;
            }

            var contained = values.Contains(value.Trim());
            if (check.Negate ? contained : !contained)
            {
                return check.Negate
                    ? "Value is in the forbidden list."
                    : $"Value is not one of the {values.Count} allowed values.";
            }

            return null;
        };
    }

    private static Func<ValidationRecord, string?> CompileCompare(string field, CompareCheck check)
    {
        return record =>
        {
            var left = record.Get(field);
            if (left is null)
            {
                return null;
            }

            string? right;
            string rightLabel;
            if (check.OtherField is { } otherField)
            {
                var (target, fieldName) = ResolveFieldPath(record, otherField);
                if (target is null)
                {
                    return $"'{otherField}' cannot be resolved from this record.";
                }

                right = target.Get(fieldName);
                rightLabel = otherField;
            }
            else
            {
                right = check.Value;
                rightLabel = $"'{check.Value}'";
            }

            if (right is null)
            {
                return null;
            }

            int comparison;
            if (check.Numeric)
            {
                // A numeric compare must never silently fall back to ordinal string
                // comparison: "1,000" vs "100" ordinal-compares as LESS (missed violation)
                // and "$50" vs "9" as LESS (false positive) — exactly the dirty values a
                // gate exists to catch. An unparseable side is its own finding, mirroring
                // the range check's behavior.
                var leftParses = decimal.TryParse(left.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var leftNumber);
                var rightParses = decimal.TryParse(right.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rightNumber);
                if (!leftParses)
                {
                    return $"Value '{left}' is not numeric, so the comparison cannot be checked.";
                }

                if (!rightParses)
                {
                    return $"Comparison reference {rightLabel} is '{right}', which is not numeric, so the comparison cannot be checked.";
                }

                comparison = leftNumber.CompareTo(rightNumber);
            }
            else
            {
                comparison = string.CompareOrdinal(left.Trim(), right.Trim());
            }

            var holds = check.Op switch
            {
                CompareOp.Eq => comparison == 0,
                CompareOp.Ne => comparison != 0,
                CompareOp.Lt => comparison < 0,
                CompareOp.Le => comparison <= 0,
                CompareOp.Gt => comparison > 0,
                CompareOp.Ge => comparison >= 0,
                _ => true,
            };

            return holds ? null : $"Value '{left}' is not {Describe(check.Op)} {rightLabel} (which is '{right}').";
        };
    }

    /// <summary>Resolves "Field", "parent.Field", "parent.parent.Field", … against the record.</summary>
    private static (ValidationRecord? Target, string Field) ResolveFieldPath(ValidationRecord record, string path)
    {
        var target = record;
        var remaining = path;
        while (remaining.StartsWith("parent.", StringComparison.OrdinalIgnoreCase))
        {
            target = target?.Parent;
            remaining = remaining["parent.".Length..];
        }

        return (target, remaining);
    }

    private static string Describe(CompareOp op) => op switch
    {
        CompareOp.Eq => "equal to",
        CompareOp.Ne => "different from",
        CompareOp.Lt => "less than",
        CompareOp.Le => "at most",
        CompareOp.Gt => "greater than",
        CompareOp.Ge => "at least",
        _ => op.ToString(),
    };
}
