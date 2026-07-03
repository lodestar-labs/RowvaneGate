namespace Rowvane.Gate.Rulesets;

/// <summary>
/// The single declarative definition of a validation: the shape of the data (entities,
/// fields, hierarchy), how each supported format carries that shape, and the rules the
/// data must satisfy. Everything the engine does is derived from this one document.
/// Rulesets can be authored by hand or generated from an XSD / JSON Schema and then edited.
/// </summary>
public sealed class RulesetDocument
{
    public required string Name { get; set; }

    public string Version { get; set; } = "1";

    public string? Description { get; set; }

    /// <summary>The entity hierarchy this ruleset validates. Children nest to any depth.</summary>
    public required EntityShape Shape { get; set; }

    public SourceOptions Source { get; set; } = new();

    public List<Rule> Rules { get; set; } = [];

    /// <summary>Findings per severity before reporting is truncated (full counts are always kept).</summary>
    public int MaxFindingsPerRule { get; set; } = 1000;

    /// <summary>All entities in parent-before-child (breadth-first) order.</summary>
    public IEnumerable<EntityShape> EnumerateEntities()
    {
        var queue = new Queue<EntityShape>();
        queue.Enqueue(Shape);
        while (queue.Count > 0)
        {
            var entity = queue.Dequeue();
            yield return entity;
            foreach (var child in entity.Children)
            {
                queue.Enqueue(child);
            }
        }
    }

    public EntityShape? FindEntity(string name) =>
        EnumerateEntities().FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parent entity of <paramref name="entity"/>, or null for the root.</summary>
    public EntityShape? FindParent(EntityShape entity) =>
        EnumerateEntities().FirstOrDefault(e => e.Children.Contains(entity));

    /// <summary>
    /// Structural validation of the document itself. Returns every problem found so
    /// authors fix them all in one pass; an empty list means the ruleset is usable.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("Ruleset 'name' is required.");
        }

        if (Shape is null)
        {
            errors.Add("Ruleset 'shape' is required.");
            return errors;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in EnumerateEntities())
        {
            if (string.IsNullOrWhiteSpace(entity.Name))
            {
                errors.Add("Every entity requires a 'name'.");
                continue;
            }

            if (!seen.Add(entity.Name))
            {
                errors.Add($"Entity '{entity.Name}': names must be unique within a ruleset.");
            }

            if (entity.Fields.Count == 0)
            {
                errors.Add($"Entity '{entity.Name}': at least one field is required.");
            }

            var seenFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in entity.Fields)
            {
                if (string.IsNullOrWhiteSpace(field.Name))
                {
                    errors.Add($"Entity '{entity.Name}': every field requires a 'name'.");
                }
                else if (!seenFields.Add(field.Name))
                {
                    errors.Add($"Entity '{entity.Name}': duplicate field '{field.Name}'.");
                }
            }
        }

        var ruleIndex = 0;
        var seenRuleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in Rules)
        {
            ruleIndex++;
            var label = rule.Id ?? $"rules[{ruleIndex - 1}]";
            if (rule.Id is not null && !seenRuleIds.Add(rule.Id))
            {
                errors.Add($"{label}: duplicate rule id.");
            }

            var entity = rule.Entity is null ? null : FindEntity(rule.Entity);
            if (rule.Entity is not null && entity is null)
            {
                errors.Add($"{label}: unknown entity '{rule.Entity}'.");
                continue;
            }

            if (rule.Check is null)
            {
                errors.Add($"{label}: 'check' is required.");
                continue;
            }

            foreach (var problem in rule.Check.Validate(this, entity, rule))
            {
                errors.Add($"{label}: {problem}");
            }
        }

        return errors;
    }

    /// <summary>Assigns stable ids (R001, R002, …) to rules that don't declare one.</summary>
    public void AssignRuleIds()
    {
        var next = 1;
        var taken = new HashSet<string>(Rules.Where(r => r.Id is not null).Select(r => r.Id!), StringComparer.OrdinalIgnoreCase);
        foreach (var rule in Rules.Where(r => string.IsNullOrWhiteSpace(r.Id)))
        {
            string candidate;
            do
            {
                candidate = $"R{next++:D3}";
            }
            while (taken.Contains(candidate));

            rule.Id = candidate;
            taken.Add(candidate);
        }
    }
}

/// <summary>One level of the hierarchy: an entity and the fields records of it carry.</summary>
public sealed class EntityShape
{
    /// <summary>Matches the XML element, JSON property, or the CSV record-type value.</summary>
    public required string Name { get; set; }

    public List<FieldShape> Fields { get; set; } = [];

    public List<EntityShape> Children { get; set; } = [];

    /// <summary>When true, a parent record without at least one instance is a finding.</summary>
    public bool Required { get; set; }

    public FieldShape? FindField(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public EntityShape? FindChild(string name) =>
        Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A field: name plus optional positional info for fixed-width layouts.</summary>
public sealed class FieldShape
{
    public required string Name { get; set; }

    /// <summary>Fixed-width only: 1-based start column.</summary>
    public int? Start { get; set; }

    /// <summary>Fixed-width only: width in characters.</summary>
    public int? Length { get; set; }
}
