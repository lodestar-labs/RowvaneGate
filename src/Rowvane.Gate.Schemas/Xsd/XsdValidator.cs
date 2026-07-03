using System.Xml;
using System.Xml.Schema;
using Rowvane.Gate.Findings;
using Rowvane.Gate.Rulesets;

namespace Rowvane.Gate.Schemas.Xsd;

/// <summary>
/// Native XSD pass-through validation: streams the document through the .NET schema
/// validator and turns every violation into a structured finding with the parser's real
/// line and column. Complements ruleset validation for consumers whose contract is the
/// XSD itself.
/// </summary>
public static class XsdValidator
{
    public const string RuleId = "XSD";

    public static async Task<IReadOnlyList<Finding>> ValidateAsync(
        Stream xmlStream,
        Stream xsdStream,
        int maxFindings = 10_000,
        CancellationToken cancellationToken = default)
    {
        var schemaSet = new XmlSchemaSet();
        var schemaProblems = new List<string>();
        schemaSet.ValidationEventHandler += (_, e) => schemaProblems.Add(e.Message);
        using (var schemaReader = XmlReader.Create(xsdStream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
        {
            schemaSet.Add(null, schemaReader);
        }

        schemaSet.Compile();
        if (schemaProblems.Count > 0)
        {
            throw new RulesetException($"XSD could not be compiled: {string.Join("; ", schemaProblems)}");
        }

        var findings = new List<Finding>();
        var settings = new XmlReaderSettings
        {
            Async = true,
            ValidationType = ValidationType.Schema,
            Schemas = schemaSet,
            DtdProcessing = DtdProcessing.Prohibit,
            CloseInput = false,
        };
        settings.ValidationFlags |= XmlSchemaValidationFlags.ReportValidationWarnings;
        settings.ValidationEventHandler += (_, e) =>
        {
            if (findings.Count >= maxFindings)
            {
                return;
            }

            findings.Add(new Finding(
                RuleId,
                e.Severity == XmlSeverityType.Error ? Severity.Error : Severity.Warning,
                Entity: "(document)",
                Field: null,
                Line: e.Exception?.LineNumber is > 0 ? e.Exception.LineNumber : null,
                Path: e.Exception?.SourceUri,
                RawValue: null,
                Message: e.Message));
        };

        using var reader = XmlReader.Create(xmlStream, settings);
        try
        {
            while (await reader.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch (XmlException ex)
        {
            findings.Add(new Finding(
                RuleId, Severity.Error, "(document)", null,
                ex.LineNumber > 0 ? ex.LineNumber : null, null, null,
                $"Malformed XML: {ex.Message}"));
        }

        return findings;
    }
}
