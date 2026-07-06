using System.Runtime.CompilerServices;
using System.Xml;
using Rowvane.Gate.Records;
using Rowvane.Gate.Rulesets;
using Rowvane.Gate.Sources;

namespace Rowvane.Gate.Readers.Xml;

/// <summary>
/// Streaming XML source: walks the document with a forward-only <see cref="XmlReader"/>,
/// materializing one root-entity subtree at a time. Real parser line numbers are captured
/// for every record (no custom attributes required in the source file). Elements matching
/// child entities recurse; elements or attributes matching shape fields become values;
/// anything else is skipped, so documents may carry extra content without breaking.
/// </summary>
public sealed class XmlValidationSource : IValidationSource
{
    public string Format => "xml";

    public IReadOnlyList<string> Extensions { get; } = ["xml"];

    public async IAsyncEnumerable<ValidationRecord> ReadAsync(
        Stream stream,
        RulesetDocument ruleset,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Prohibit,
            CloseInput = false,
        };

        using var reader = XmlReader.Create(stream, settings);
        var root = ruleset.Shape;
        var wrapper = ruleset.Source.Xml.RootElement;
        var insideWrapper = wrapper is null;
        var wrapperDepth = -1;
        var readNext = true;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (readNext && !await ReadOrThrowAsync(reader))
            {
                break;
            }

            readNext = true;
            if (wrapper is not null)
            {
                if (!insideWrapper
                    && reader.NodeType == XmlNodeType.Element
                    && string.Equals(reader.LocalName, wrapper, StringComparison.OrdinalIgnoreCase))
                {
                    insideWrapper = true;
                    wrapperDepth = reader.Depth;
                    continue;
                }

                if (insideWrapper
                    && reader.NodeType == XmlNodeType.EndElement
                    && reader.Depth == wrapperDepth
                    && string.Equals(reader.LocalName, wrapper, StringComparison.OrdinalIgnoreCase))
                {
                    insideWrapper = false;
                    continue;
                }
            }

            if (insideWrapper
                && reader.NodeType == XmlNodeType.Element
                && string.Equals(reader.LocalName, root.Name, StringComparison.OrdinalIgnoreCase))
            {
                var record = await ReadEntityAsync(reader, root, parent: null, parentPath: null, cancellationToken);
                readNext = false;
                yield return record;
            }
        }
    }

    private static async Task<ValidationRecord> ReadEntityAsync(
        XmlReader reader,
        EntityShape entity,
        ValidationRecord? parent,
        string? parentPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lineInfo = reader as IXmlLineInfo;
        long? line = lineInfo?.HasLineInfo() == true ? lineInfo.LineNumber : null;
        var path = parentPath is null ? entity.Name : $"{parentPath}/{entity.Name}";
        var record = new ValidationRecord
        {
            Entity = entity,
            Location = new SourceLocation(line, path),
            Parent = parent,
        };

        if (reader.HasAttributes)
        {
            while (reader.MoveToNextAttribute())
            {
                if (entity.FindField(reader.LocalName) is { } field)
                {
                    // Same empty-to-null coercion as the element path below: an empty
                    // attribute must validate identically to an empty element.
                    record.Fields[field.Name] = string.IsNullOrEmpty(reader.Value) ? null : reader.Value;
                }
            }

            reader.MoveToElement();
        }

        if (reader.IsEmptyElement)
        {
            await ReadOrThrowAsync(reader);
            return record;
        }

        var entityDepth = reader.Depth;
        await ReadOrThrowAsync(reader);
        while (!(reader.NodeType == XmlNodeType.EndElement && reader.Depth == entityDepth))
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                var name = reader.LocalName;
                if (entity.FindChild(name) is { } childEntity)
                {
                    var child = await ReadEntityAsync(reader, childEntity, record, path, cancellationToken);
                    record.Children.Add(child);
                }
                else if (entity.FindField(name) is { } field)
                {
                    try
                    {
                        var text = await reader.ReadElementContentAsStringAsync();
                        record.Fields[field.Name] = string.IsNullOrEmpty(text) ? null : text;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or XmlException)
                    {
                        throw new SourceFormatException(
                            $"Element '{name}' at {record.Location} maps to field '{field.Name}' but does not contain simple text content.",
                            ex);
                    }
                }
                else
                {
                    await reader.SkipAsync();
                }
            }
            else if (!await ReadOrThrowAsync(reader))
            {
                throw new SourceFormatException($"Unexpected end of document inside element '{entity.Name}'.");
            }
        }

        await ReadOrThrowAsync(reader);
        return record;
    }

    private static async Task<bool> ReadOrThrowAsync(XmlReader reader)
    {
        try
        {
            return await reader.ReadAsync();
        }
        catch (XmlException ex)
        {
            throw new SourceFormatException($"Malformed XML at line {ex.LineNumber}: {ex.Message}", ex);
        }
    }
}
