using System.Globalization;
using System.Text.Json;
using Rowvane.Gate.Rulesets;

namespace Rowvane.Gate.Schemas.Json;

/// <summary>
/// Generates an editable ruleset from a JSON Schema document (the widely used subset:
/// type / properties / required / items / enum / pattern / minimum / maximum /
/// minLength / maxLength / format). Object properties become fields, object- or
/// array-of-object properties become child entities. As with XSD import, the generated
/// ruleset then validates any supported format of the same data.
/// </summary>
public static class JsonSchemaImporter
{
    public static RulesetDocument Import(string schemaJson, string rulesetName, string rootEntityName = "Record")
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(schemaJson, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            throw new RulesetException($"JSON Schema is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            var schema = root;

            // Accept either an object schema or an array-of-object schema at the top.
            if (TypeOf(schema) == "array" && schema.TryGetProperty("items", out var items))
            {
                schema = items;
            }

            if (TypeOf(schema) != "object")
            {
                throw new RulesetException("JSON Schema import requires an object schema (or an array of objects) at the root.");
            }

            var ruleset = new RulesetDocument
            {
                Name = rulesetName,
                Description = "Generated from JSON Schema. Edit freely — this is a native ruleset.",
                Shape = null!,
            };

            var rules = new List<Rule>();
            ruleset.Shape = BuildEntity(schema, rootEntityName, rules);
            ruleset.Rules = rules;
            ruleset.AssignRuleIds();

            var errors = ruleset.Validate();
            if (errors.Count > 0)
            {
                throw new RulesetException(
                    $"Import produced an invalid ruleset:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", errors)}");
            }

            return ruleset;
        }
    }

    private static EntityShape BuildEntity(JsonElement objectSchema, string name, List<Rule> rules)
    {
        var entity = new EntityShape { Name = name };
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (objectSchema.TryGetProperty("required", out var requiredArray) && requiredArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in requiredArray.EnumerateArray())
            {
                if (item.GetString() is { } propertyName)
                {
                    required.Add(propertyName);
                }
            }
        }

        if (!objectSchema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return entity;
        }

        foreach (var property in properties.EnumerateObject())
        {
            var schema = property.Value;
            var type = TypeOf(schema);

            if (type == "object")
            {
                var child = BuildEntity(schema, property.Name, rules);
                child.Required = required.Contains(property.Name);
                entity.Children.Add(child);
                continue;
            }

            if (type == "array" && schema.TryGetProperty("items", out var items) && TypeOf(items) == "object")
            {
                var child = BuildEntity(items, property.Name, rules);
                child.Required = required.Contains(property.Name);
                entity.Children.Add(child);
                continue;
            }

            entity.Fields.Add(new FieldShape { Name = property.Name });
            if (required.Contains(property.Name))
            {
                rules.Add(new Rule { Entity = name, Field = property.Name, Check = new RequiredCheck() });
            }

            rules.AddRange(RulesFromPropertySchema(name, property.Name, schema, type));
        }

        return entity;
    }

    private static IEnumerable<Rule> RulesFromPropertySchema(string entity, string field, JsonElement schema, string? type)
    {
        var kind = type switch
        {
            "integer" => DataKind.Integer,
            "number" => DataKind.Decimal,
            "boolean" => DataKind.Boolean,
            "string" when Format(schema) == "date" => DataKind.Date,
            "string" when Format(schema) == "date-time" => DataKind.DateTime,
            "string" when Format(schema) == "time" => DataKind.Time,
            "string" when Format(schema) == "uuid" => DataKind.Guid,
            _ => (DataKind?)null,
        };
        if (kind is not null && kind != DataKind.String)
        {
            yield return new Rule { Entity = entity, Field = field, Check = new TypeCheck { Kind = kind.Value } };
        }

        if (schema.TryGetProperty("enum", out var enumeration) && enumeration.ValueKind == JsonValueKind.Array)
        {
            var values = enumeration.EnumerateArray()
                .Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())
                .Where(v => v is not null)
                .Select(v => v!)
                .ToList();
            if (values.Count > 0)
            {
                yield return new Rule
                {
                    Entity = entity,
                    Field = field,
                    Check = new InListCheck { Values = values, CaseInsensitive = false },
                };
            }
        }

        if (schema.TryGetProperty("pattern", out var pattern) && pattern.GetString() is { } patternValue)
        {
            yield return new Rule { Entity = entity, Field = field, Check = new RegexCheck { Pattern = patternValue } };
        }

        // Draft 6+ uses numeric exclusiveMinimum/Maximum; draft 4 pairs a numeric
        // minimum/maximum with a boolean exclusive flag. Both are honored.
        var minimum = ReadNumber(schema, "minimum");
        var maximum = ReadNumber(schema, "maximum");
        var exclusiveMinimum = ReadNumber(schema, "exclusiveMinimum");
        var exclusiveMaximum = ReadNumber(schema, "exclusiveMaximum");
        decimal? min = minimum ?? exclusiveMinimum;
        decimal? max = maximum ?? exclusiveMaximum;
        if (min is not null || max is not null)
        {
            yield return new Rule
            {
                Entity = entity,
                Field = field,
                Check = new RangeCheck
                {
                    Min = min,
                    Max = max,
                    ExclusiveMin = (minimum is null && exclusiveMinimum is not null) || IsTrue(schema, "exclusiveMinimum"),
                    ExclusiveMax = (maximum is null && exclusiveMaximum is not null) || IsTrue(schema, "exclusiveMaximum"),
                },
            };
        }

        int? minLength = ReadInt(schema, "minLength");
        int? maxLength = ReadInt(schema, "maxLength");
        if (minLength is not null || maxLength is not null)
        {
            yield return new Rule
            {
                Entity = entity,
                Field = field,
                Check = new LengthCheck { Min = minLength, Max = maxLength },
            };
        }
    }

    private static string? TypeOf(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("type", out var type)
            ? type.ValueKind switch
            {
                JsonValueKind.String => type.GetString(),
                // ["string","null"] style unions: take the first non-null type.
                JsonValueKind.Array => type.EnumerateArray()
                    .Select(t => t.GetString())
                    .FirstOrDefault(t => t is not null and not "null"),
                _ => null,
            }
            : null;

    private static bool IsTrue(JsonElement schema, string property) =>
        schema.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Format(JsonElement schema) =>
        schema.TryGetProperty("format", out var format) ? format.GetString() : null;

    private static decimal? ReadNumber(JsonElement schema, string property) =>
        schema.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : value.ValueKind == JsonValueKind.String
                && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;

    private static int? ReadInt(JsonElement schema, string property)
    {
        if (!schema.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        // GetInt32() throws FormatException on 5.5 or 1e10, which escaped the import
        // endpoint as a 500. A non-integer facet is a schema problem the caller should
        // see as a clean import error instead.
        if (!value.TryGetInt32(out var parsed))
        {
            throw new RulesetException(
                $"JSON Schema facet '{property}' must be a whole number within Int32 range; got '{value.GetRawText()}'.");
        }

        return parsed;
    }
}
