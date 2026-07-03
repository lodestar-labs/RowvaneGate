using System.Xml;
using System.Xml.Schema;
using Rowvane.Gate.Rulesets;

namespace Rowvane.Gate.Schemas.Xsd;

/// <summary>
/// Generates an editable ruleset from an XSD: the element hierarchy becomes the shape,
/// and the schema's constraints become native rules — minOccurs ⇒ required, enumerations
/// ⇒ inList, patterns ⇒ regex, value facets ⇒ range/length, base types ⇒ dataType.
/// The point: import the XSD once, then validate CSV or JSON representations of the same
/// data against the identical rules — something XSD alone can never do.
/// </summary>
public static class XsdImporter
{
    public static RulesetDocument Import(Stream xsdStream, string rulesetName, string? rootElementName = null)
    {
        var schemaSet = new XmlSchemaSet();
        var problems = new List<string>();
        schemaSet.ValidationEventHandler += (_, e) => problems.Add(e.Message);
        using (var reader = XmlReader.Create(xsdStream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
        {
            schemaSet.Add(null, reader);
        }

        schemaSet.Compile();
        if (problems.Count > 0)
        {
            throw new RulesetException($"XSD could not be compiled: {string.Join("; ", problems)}");
        }

        var globals = schemaSet.GlobalElements.Values.Cast<XmlSchemaElement>().ToList();
        var rootElement = rootElementName is null
            ? globals.Count == 1
                ? globals[0]
                : throw new RulesetException(
                    $"The schema declares {globals.Count} global elements ({string.Join(", ", globals.Select(g => g.Name))}); specify which is the root.")
            : globals.FirstOrDefault(g => string.Equals(g.Name, rootElementName, StringComparison.OrdinalIgnoreCase))
                ?? throw new RulesetException($"Global element '{rootElementName}' was not found in the schema.");

        var document = new RulesetDocument
        {
            Name = rulesetName,
            Description = $"Generated from XSD (root element '{rootElement.Name}'). Edit freely — this is a native ruleset.",
            Shape = null!,
        };

        var rules = new List<Rule>();
        document.Shape = BuildEntity(rootElement, rules, new HashSet<XmlSchemaType>());
        document.Rules = rules;
        document.AssignRuleIds();

        var errors = document.Validate();
        if (errors.Count > 0)
        {
            throw new RulesetException(
                $"Import produced an invalid ruleset (schema shape not supported):{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", errors)}");
        }

        return document;
    }

    private static EntityShape BuildEntity(XmlSchemaElement element, List<Rule> rules, HashSet<XmlSchemaType> visiting)
    {
        var entity = new EntityShape { Name = element.Name ?? element.QualifiedName.Name };

        if (element.ElementSchemaType is XmlSchemaComplexType complexType)
        {
            // Guard against recursive type definitions.
            if (!visiting.Add(complexType))
            {
                return entity;
            }

            foreach (var attribute in complexType.AttributeUses.Values.Cast<XmlSchemaAttribute>())
            {
                AddField(entity, rules, attribute.Name ?? attribute.QualifiedName.Name,
                    attribute.AttributeSchemaType, required: attribute.Use == XmlSchemaUse.Required);
            }

            foreach (var child in EnumerateChildElements(complexType.ContentTypeParticle))
            {
                if (child.ElementSchemaType is XmlSchemaComplexType childComplex && HasChildElements(childComplex))
                {
                    var childEntity = BuildEntity(child, rules, visiting);
                    childEntity.Required = child.MinOccurs >= 1;
                    entity.Children.Add(childEntity);
                }
                else
                {
                    AddField(entity, rules, child.Name ?? child.QualifiedName.Name,
                        child.ElementSchemaType as XmlSchemaSimpleType, required: child.MinOccurs >= 1);
                }
            }

            visiting.Remove(complexType);
        }
        else
        {
            // A simple-typed root: model it as a single-field entity.
            AddField(entity, rules, entity.Name, element.ElementSchemaType as XmlSchemaSimpleType, required: true);
        }

        return entity;

        void AddField(EntityShape target, List<Rule> ruleSink, string name, XmlSchemaType? type, bool required)
        {
            if (target.FindField(name) is not null)
            {
                return;
            }

            target.Fields.Add(new FieldShape { Name = name });
            if (required)
            {
                ruleSink.Add(new Rule { Entity = target.Name, Field = name, Check = new RequiredCheck() });
            }

            if (type is XmlSchemaSimpleType simpleType)
            {
                ruleSink.AddRange(RulesFromSimpleType(target.Name, name, simpleType));
            }
        }
    }

    /// <summary>A child element becomes an entity when its type contains child elements of its own.</summary>
    private static bool HasChildElements(XmlSchemaComplexType type) =>
        EnumerateChildElements(type.ContentTypeParticle).Any();

    private static IEnumerable<XmlSchemaElement> EnumerateChildElements(XmlSchemaParticle? particle)
    {
        switch (particle)
        {
            case XmlSchemaElement element:
                yield return element;
                break;

            case XmlSchemaGroupBase group:
                foreach (var item in group.Items)
                {
                    foreach (var nested in EnumerateChildElements(item as XmlSchemaParticle))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    private static IEnumerable<Rule> RulesFromSimpleType(string entity, string field, XmlSchemaSimpleType simpleType)
    {
        if (MapDataKind(simpleType.TypeCode) is { } kind && kind != DataKind.String)
        {
            yield return new Rule { Entity = entity, Field = field, Check = new TypeCheck { Kind = kind } };
        }

        if (simpleType.Content is not XmlSchemaSimpleTypeRestriction restriction)
        {
            yield break;
        }

        var enumeration = new List<string>();
        decimal? min = null, max = null;
        var exclusiveMin = false;
        var exclusiveMax = false;
        int? minLength = null, maxLength = null;

        foreach (XmlSchemaFacet facet in restriction.Facets)
        {
            switch (facet)
            {
                case XmlSchemaEnumerationFacet e when e.Value is not null:
                    enumeration.Add(e.Value);
                    break;
                case XmlSchemaPatternFacet p when p.Value is not null:
                    yield return new Rule { Entity = entity, Field = field, Check = new RegexCheck { Pattern = $"^(?:{p.Value})$" } };
                    break;
                case XmlSchemaMinInclusiveFacet f when decimal.TryParse(f.Value, System.Globalization.CultureInfo.InvariantCulture, out var v):
                    min = v;
                    break;
                case XmlSchemaMaxInclusiveFacet f when decimal.TryParse(f.Value, System.Globalization.CultureInfo.InvariantCulture, out var v):
                    max = v;
                    break;
                case XmlSchemaMinExclusiveFacet f when decimal.TryParse(f.Value, System.Globalization.CultureInfo.InvariantCulture, out var v):
                    min = v;
                    exclusiveMin = true;
                    break;
                case XmlSchemaMaxExclusiveFacet f when decimal.TryParse(f.Value, System.Globalization.CultureInfo.InvariantCulture, out var v):
                    max = v;
                    exclusiveMax = true;
                    break;
                case XmlSchemaLengthFacet f when int.TryParse(f.Value, out var v):
                    minLength = v;
                    maxLength = v;
                    break;
                case XmlSchemaMinLengthFacet f when int.TryParse(f.Value, out var v):
                    minLength = v;
                    break;
                case XmlSchemaMaxLengthFacet f when int.TryParse(f.Value, out var v):
                    maxLength = v;
                    break;
            }
        }

        if (enumeration.Count > 0)
        {
            yield return new Rule
            {
                Entity = entity,
                Field = field,
                Check = new InListCheck { Values = enumeration, CaseInsensitive = false },
            };
        }

        if (min is not null || max is not null)
        {
            yield return new Rule
            {
                Entity = entity,
                Field = field,
                Check = new RangeCheck { Min = min, Max = max, ExclusiveMin = exclusiveMin, ExclusiveMax = exclusiveMax },
            };
        }

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

    private static DataKind? MapDataKind(XmlTypeCode typeCode) => typeCode switch
    {
        XmlTypeCode.Byte or XmlTypeCode.Short or XmlTypeCode.Int or XmlTypeCode.Long
            or XmlTypeCode.UnsignedByte or XmlTypeCode.UnsignedShort or XmlTypeCode.UnsignedInt
            or XmlTypeCode.UnsignedLong or XmlTypeCode.Integer or XmlTypeCode.NonNegativeInteger
            or XmlTypeCode.PositiveInteger or XmlTypeCode.NonPositiveInteger or XmlTypeCode.NegativeInteger
            => DataKind.Integer,
        XmlTypeCode.Decimal or XmlTypeCode.Float or XmlTypeCode.Double => DataKind.Decimal,
        XmlTypeCode.Boolean => DataKind.Boolean,
        XmlTypeCode.Date => DataKind.Date,
        XmlTypeCode.DateTime => DataKind.DateTime,
        XmlTypeCode.Time => DataKind.Time,
        XmlTypeCode.String or XmlTypeCode.NormalizedString or XmlTypeCode.Token => DataKind.String,
        _ => null,
    };
}
