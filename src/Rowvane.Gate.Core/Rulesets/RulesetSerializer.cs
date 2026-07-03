using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rowvane.Gate.Rulesets;

/// <summary>Canonical JSON (de)serialization for ruleset documents.</summary>
public static class RulesetSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static RulesetDocument Deserialize(string json)
    {
        RulesetDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<RulesetDocument>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new RulesetException($"Ruleset is not valid JSON: {ex.Message}");
        }

        return Finish(document);
    }

    public static async Task<RulesetDocument> DeserializeAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        RulesetDocument? document;
        try
        {
            document = await JsonSerializer.DeserializeAsync<RulesetDocument>(stream, Options, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new RulesetException($"Ruleset is not valid JSON: {ex.Message}");
        }

        return Finish(document);
    }

    public static string Serialize(RulesetDocument document) => JsonSerializer.Serialize(document, Options);

    private static RulesetDocument Finish(RulesetDocument? document)
    {
        if (document is null)
        {
            throw new RulesetException("Ruleset document is empty.");
        }

        var errors = document.Validate();
        if (errors.Count > 0)
        {
            throw new RulesetException(
                $"Ruleset '{document.Name}' is invalid:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", errors)}");
        }

        document.AssignRuleIds();
        return document;
    }
}

public sealed class RulesetException(string message) : Exception(message);

/// <summary>
/// Compile-time ruleset: implementing types declare their document through a static
/// abstract member, so rulesets can ship inside an application with compiler-checked
/// existence — no files, no runtime discovery.
/// </summary>
public interface IRuleset
{
    static abstract RulesetDocument Document { get; }
}
