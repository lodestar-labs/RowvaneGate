using System.Diagnostics;
using System.Globalization;
using Rowvane.Gate.Findings;
using Rowvane.Gate.Records;
using Rowvane.Gate.Rulesets;
using Rowvane.Gate.Sources;
using Microsoft.Extensions.Logging;

namespace Rowvane.Gate.Engine;

/// <summary>
/// Runs one file through one ruleset in a single streaming pass. Per-record rules are
/// compiled once and evaluated as each subtree arrives; dataset-scoped rules (uniqueness,
/// row counts) accumulate as the stream flows and finalize at the end; hierarchy rules
/// (required children, aggregates) evaluate per completed subtree. Memory is bounded by
/// one root subtree plus the uniqueness keys.
/// </summary>
public sealed class ValidationEngine(
    IEnumerable<IValidationSource> sources,
    IEnumerable<IAnalyticsRunner> analyticsRunners,
    ILogger<ValidationEngine> logger)
{
    private readonly IAnalyticsRunner? _analytics = analyticsRunners.FirstOrDefault();

    public async Task<ValidationReport> ValidateAsync(
        Stream stream,
        string format,
        RulesetDocument ruleset,
        string sourceName,
        string? stagedFilePath = null,
        CancellationToken cancellationToken = default)
    {
        var source = sources.FirstOrDefault(s => string.Equals(s.Format, format, StringComparison.OrdinalIgnoreCase))
            ?? throw new SourceFormatException($"No source reader is registered for format '{format}'.");

        var report = new ValidationReport
        {
            Ruleset = ruleset.Name,
            Source = sourceName,
            Format = format.ToLowerInvariant(),
            StartedAt = DateTimeOffset.UtcNow,
        };

        var stopwatch = Stopwatch.StartNew();
        var plan = new RulePlan(ruleset);

        await foreach (var root in source.ReadAsync(stream, ruleset, cancellationToken))
        {
            foreach (var record in root.SelfAndDescendants())
            {
                report.RecordsRead++;
                var count = report.RecordCounts.TryGetValue(record.Entity.Name, out var existing) ? existing : 0;
                report.RecordCounts[record.Entity.Name] = count + 1;

                plan.EvaluateRecord(record, report);
            }

            plan.EvaluateSubtree(root, report);
        }

        plan.Finalize(report);
        await RunSqlChecksAsync(ruleset, plan, report, stagedFilePath, cancellationToken);

        stopwatch.Stop();
        report.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
        logger.LogInformation(
            "Validated {Source} against {Ruleset}: {Records} records, {Errors} errors, {Warnings} warnings in {ElapsedMs:F0} ms",
            sourceName, ruleset.Name, report.RecordsRead, report.ErrorCount, report.WarningCount, report.DurationMs);
        return report;
    }

    private async Task RunSqlChecksAsync(
        RulesetDocument ruleset,
        RulePlan plan,
        ValidationReport report,
        string? stagedFilePath,
        CancellationToken cancellationToken)
    {
        foreach (var rule in plan.SqlRules)
        {
            var check = (SqlCheck)rule.Check;
            if (_analytics is null || stagedFilePath is null)
            {
                report.Add(new Finding(
                    rule.Id!, Severity.Warning, rule.Entity ?? "(dataset)", null, null, null, null,
                    _analytics is null
                        ? "SQL rule skipped: the analytics engine is not enabled."
                        : "SQL rule skipped: no staged file was available."),
                    ruleset.MaxFindingsPerRule);
                continue;
            }

            IReadOnlyList<string> violations;
            try
            {
                violations = await _analytics.RunSqlCheckAsync(stagedFilePath, report.Format, ruleset, check, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.Add(new Finding(
                    rule.Id!, Severity.Error, rule.Entity ?? "(dataset)", null, null, null, null,
                    $"SQL rule failed to execute: {ex.Message}"), ruleset.MaxFindingsPerRule);
                continue;
            }

            foreach (var message in violations)
            {
                report.Add(new Finding(
                    rule.Id!, rule.Severity, rule.Entity ?? "(dataset)", null, null, null, null,
                    rule.Message ?? message), ruleset.MaxFindingsPerRule);
            }
        }
    }

    /// <summary>
    /// The compiled execution plan for one run: per-entity record rules, uniqueness and
    /// row-count accumulators, per-subtree aggregate rules, and the shape's required-child
    /// structure — all resolved before the first record flows.
    /// </summary>
    private sealed class RulePlan
    {
        public const string ShapeRuleId = "SHAPE";

        private readonly RulesetDocument _ruleset;
        private readonly Dictionary<string, List<CompiledRule>> _recordRules = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(Rule Rule, UniqueCheck Check, HashSet<string> Seen)> _datasetUnique = [];
        private readonly List<(Rule Rule, UniqueCheck Check)> _parentUnique = [];
        private readonly List<(Rule Rule, AggregateCheck Check, Func<ValidationRecord, bool>? When)> _aggregates = [];
        private readonly List<(Rule Rule, RowCountCheck Check)> _rowCounts = [];
        private readonly Dictionary<string, List<EntityShape>> _requiredChildren = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _entityCounts = new(StringComparer.OrdinalIgnoreCase);

        public List<Rule> SqlRules { get; } = [];

        public RulePlan(RulesetDocument ruleset)
        {
            _ruleset = ruleset;
            foreach (var rule in ruleset.Rules)
            {
                switch (rule.Check)
                {
                    case UniqueCheck { Scope: UniqueScope.Dataset } unique:
                        _datasetUnique.Add((rule, unique, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
                        break;
                    case UniqueCheck unique:
                        _parentUnique.Add((rule, unique));
                        break;
                    case AggregateCheck aggregate:
                        _aggregates.Add((rule, aggregate, CompiledRule.CompileCondition(rule.When)));
                        break;
                    case RowCountCheck rowCount:
                        _rowCounts.Add((rule, rowCount));
                        break;
                    case SqlCheck:
                        SqlRules.Add(rule);
                        break;
                    default:
                        if (CompiledRule.TryCompile(rule) is { } compiled && rule.Entity is not null)
                        {
                            if (!_recordRules.TryGetValue(rule.Entity, out var list))
                            {
                                _recordRules[rule.Entity] = list = [];
                            }

                            list.Add(compiled);
                        }

                        break;
                }
            }

            foreach (var entity in ruleset.EnumerateEntities())
            {
                var required = entity.Children.Where(c => c.Required).ToList();
                if (required.Count > 0)
                {
                    _requiredChildren[entity.Name] = required;
                }
            }
        }

        public void EvaluateRecord(ValidationRecord record, ValidationReport report)
        {
            var entityCount = _entityCounts.TryGetValue(record.Entity.Name, out var count) ? count : 0;
            _entityCounts[record.Entity.Name] = entityCount + 1;

            if (_recordRules.TryGetValue(record.Entity.Name, out var rules))
            {
                foreach (var rule in rules)
                {
                    if (rule.Evaluate(record) is { } finding)
                    {
                        report.Add(finding, _ruleset.MaxFindingsPerRule);
                    }
                }
            }

            foreach (var (rule, check, seen) in _datasetUnique)
            {
                if (!string.Equals(rule.Entity, record.Entity.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var key = BuildKey(record, check.Fields);
                if (key is not null && !seen.Add(key))
                {
                    report.Add(new Finding(
                        rule.Id!, rule.Severity, record.Entity.Name, string.Join("+", check.Fields),
                        record.Location.Line, record.Location.Path, key,
                        rule.Message ?? $"Duplicate value for ({string.Join(", ", check.Fields)})."),
                        _ruleset.MaxFindingsPerRule);
                }
            }

            if (_requiredChildren.TryGetValue(record.Entity.Name, out var requiredChildren))
            {
                foreach (var child in requiredChildren)
                {
                    if (!record.Children.Any(c => ReferenceEquals(c.Entity, child) ||
                            string.Equals(c.Entity.Name, child.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        report.Add(new Finding(
                            ShapeRuleId, Severity.Error, record.Entity.Name, child.Name,
                            record.Location.Line, record.Location.Path, null,
                            $"At least one '{child.Name}' child record is required."),
                            _ruleset.MaxFindingsPerRule);
                    }
                }
            }
        }

        /// <summary>Rules that need a completed subtree: parent-scoped uniqueness and aggregates.</summary>
        public void EvaluateSubtree(ValidationRecord root, ValidationReport report)
        {
            foreach (var record in root.SelfAndDescendants())
            {
                foreach (var (rule, check) in _parentUnique)
                {
                    if (!string.Equals(rule.Entity, record.Entity.Name, StringComparison.OrdinalIgnoreCase)
                        || record.Parent is null)
                    {
                        continue;
                    }

                    // Only inspect once per parent: when this record is the first sibling.
                    if (!ReferenceEquals(record.Parent.Children.First(c => c.Entity == record.Entity), record))
                    {
                        continue;
                    }

                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var sibling in record.Parent.Children.Where(c => c.Entity == record.Entity))
                    {
                        var key = BuildKey(sibling, check.Fields);
                        if (key is not null && !seen.Add(key))
                        {
                            report.Add(new Finding(
                                rule.Id!, rule.Severity, sibling.Entity.Name, string.Join("+", check.Fields),
                                sibling.Location.Line, sibling.Location.Path, key,
                                rule.Message ?? $"Duplicate value for ({string.Join(", ", check.Fields)}) under the same parent."),
                                _ruleset.MaxFindingsPerRule);
                        }
                    }
                }

                foreach (var (rule, check, when) in _aggregates)
                {
                    if (!string.Equals(rule.Entity, record.Entity.Name, StringComparison.OrdinalIgnoreCase)
                        || (when is not null && !when(record)))
                    {
                        continue;
                    }

                    EvaluateAggregate(rule, check, record, report);
                }
            }
        }

        public void Finalize(ValidationReport report)
        {
            foreach (var (rule, check) in _rowCounts)
            {
                var count = _entityCounts.TryGetValue(rule.Entity!, out var value) ? value : 0;
                if (check.Min is { } min && count < min)
                {
                    report.Add(new Finding(
                        rule.Id!, rule.Severity, rule.Entity!, null, null, null, count.ToString(),
                        rule.Message ?? $"File contains {count} '{rule.Entity}' records; at least {min} required."),
                        _ruleset.MaxFindingsPerRule);
                }

                if (check.Max is { } max && count > max)
                {
                    report.Add(new Finding(
                        rule.Id!, rule.Severity, rule.Entity!, null, null, null, count.ToString(),
                        rule.Message ?? $"File contains {count} '{rule.Entity}' records; at most {max} allowed."),
                        _ruleset.MaxFindingsPerRule);
                }
            }
        }

        private void EvaluateAggregate(Rule rule, AggregateCheck check, ValidationRecord parent, ValidationReport report)
        {
            var parentRaw = parent.Get(rule.Field!);
            if (parentRaw is null
                || !decimal.TryParse(parentRaw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parentValue))
            {
                return;
            }

            var children = parent.Children
                .Where(c => string.Equals(c.Entity.Name, check.ChildEntity, StringComparison.OrdinalIgnoreCase))
                .ToList();

            decimal aggregate;
            if (check.Function == AggregateFunction.Count)
            {
                aggregate = children.Count;
            }
            else
            {
                var values = children
                    .Select(c => c.Get(check.ChildField!))
                    .Where(v => v is not null)
                    .Select(v => decimal.TryParse(v!.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (decimal?)null)
                    .Where(d => d is not null)
                    .Select(d => d!.Value)
                    .ToList();
                if (values.Count == 0)
                {
                    return;
                }

                aggregate = check.Function switch
                {
                    AggregateFunction.Sum => values.Sum(),
                    AggregateFunction.Avg => values.Average(),
                    AggregateFunction.Min => values.Min(),
                    AggregateFunction.Max => values.Max(),
                    _ => values.Sum(),
                };
            }

            string? message = null;
            if (check.DeviationPercent is { } deviation)
            {
                var reference = parentValue == 0 ? 1 : Math.Abs(parentValue);
                var actualDeviation = Math.Abs(aggregate - parentValue) / reference * 100;
                if (actualDeviation > deviation)
                {
                    message = $"{check.Function}({check.ChildEntity}.{check.ChildField ?? "*"}) = {aggregate} deviates " +
                              $"{actualDeviation:F1}% from '{rule.Field}' = {parentValue} (allowed {deviation}%).";
                }
            }
            else
            {
                var comparison = aggregate.CompareTo(parentValue);
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
                if (!holds)
                {
                    message = $"{check.Function}({check.ChildEntity}.{check.ChildField ?? "*"}) = {aggregate} " +
                              $"violates '{check.Op}' against '{rule.Field}' = {parentValue}.";
                }
            }

            if (message is not null)
            {
                report.Add(new Finding(
                    rule.Id!, rule.Severity, parent.Entity.Name, rule.Field,
                    parent.Location.Line, parent.Location.Path, parentRaw,
                    rule.Message ?? message), _ruleset.MaxFindingsPerRule);
            }
        }

        private static string? BuildKey(ValidationRecord record, List<string> fields)
        {
            var parts = new string?[fields.Count];
            var anyValue = false;
            for (var i = 0; i < fields.Count; i++)
            {
                parts[i] = record.Get(fields[i]);
                anyValue |= parts[i] is not null;
            }

            return anyValue ? string.Join("\u001f", parts.Select(p => p ?? string.Empty)) : null;
        }
    }
}
