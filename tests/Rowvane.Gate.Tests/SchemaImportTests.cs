using Rowvane.Gate.Rulesets;
using Rowvane.Gate.Schemas.Json;
using Rowvane.Gate.Schemas.Xsd;

namespace Rowvane.Gate.Tests;

[TestFixture]
public class XsdImporterTests
{
    private const string Xsd = """
        <?xml version="1.0"?>
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xs:element name="Trip">
            <xs:complexType>
              <xs:sequence>
                <xs:element name="TripId">
                  <xs:simpleType>
                    <xs:restriction base="xs:string">
                      <xs:pattern value="T-\d+"/>
                      <xs:maxLength value="10"/>
                    </xs:restriction>
                  </xs:simpleType>
                </xs:element>
                <xs:element name="Vessel" minOccurs="0" type="xs:string"/>
                <xs:element name="Year">
                  <xs:simpleType>
                    <xs:restriction base="xs:int">
                      <xs:minInclusive value="2000"/>
                      <xs:maxInclusive value="2100"/>
                    </xs:restriction>
                  </xs:simpleType>
                </xs:element>
                <xs:element name="Gear">
                  <xs:simpleType>
                    <xs:restriction base="xs:string">
                      <xs:enumeration value="OTB"/>
                      <xs:enumeration value="PTM"/>
                    </xs:restriction>
                  </xs:simpleType>
                </xs:element>
                <xs:element name="Haul" minOccurs="0" maxOccurs="unbounded">
                  <xs:complexType>
                    <xs:sequence>
                      <xs:element name="HaulNo" type="xs:int"/>
                    </xs:sequence>
                  </xs:complexType>
                </xs:element>
              </xs:sequence>
            </xs:complexType>
          </xs:element>
        </xs:schema>
        """;

    [Test]
    public void Xsd_becomes_shape_and_native_rules()
    {
        var ruleset = XsdImporter.Import(TestData.AsStream(Xsd), "trips-from-xsd");

        Assert.Multiple(() =>
        {
            Assert.That(ruleset.Shape.Name, Is.EqualTo("Trip"));
            Assert.That(ruleset.Shape.Fields.Select(f => f.Name),
                Is.EquivalentTo(new[] { "TripId", "Vessel", "Year", "Gear" }));
            Assert.That(ruleset.Shape.Children.Single().Name, Is.EqualTo("Haul"));

            Assert.That(ruleset.Rules.Any(r => r is { Field: "TripId", Check: RequiredCheck }), "minOccurs=1 → required");
            Assert.That(ruleset.Rules.Any(r => r is { Field: "Vessel", Check: RequiredCheck }), Is.False, "minOccurs=0 → optional");
            Assert.That(ruleset.Rules.Any(r => r is { Field: "TripId", Check: RegexCheck }), "pattern facet");
            Assert.That(ruleset.Rules.Any(r => r is { Field: "TripId", Check: LengthCheck { Max: 10 } }), "maxLength facet");
            Assert.That(ruleset.Rules.Any(r => r is { Field: "Year", Check: RangeCheck { Min: 2000, Max: 2100 } }), "value facets");
            Assert.That(ruleset.Rules.Any(r => r is { Field: "Year", Check: TypeCheck { Kind: DataKind.Integer } }), "xs:int");
            Assert.That(ruleset.Rules.Any(r =>
                r is { Field: "Gear", Check: InListCheck { Values.Count: 2 } }), "enumeration");
            Assert.That(ruleset.Validate(), Is.Empty, "the generated ruleset is valid");
        });
    }

    [Test]
    public async Task Native_xsd_validation_reports_real_line_numbers()
    {
        const string invalid = """
            <Trip>
              <TripId>T-1</TripId>
              <Year>1999</Year>
              <Gear>NET</Gear>
            </Trip>
            """;

        var findings = await XsdValidator.ValidateAsync(TestData.AsStream(invalid), TestData.AsStream(Xsd));

        Assert.Multiple(() =>
        {
            Assert.That(findings, Is.Not.Empty);
            Assert.That(findings.All(f => f.RuleId == "XSD"));
            Assert.That(findings.Any(f => f.Line == 3), "the 1999 violation carries its line");
        });
    }
}

[TestFixture]
public class JsonSchemaImporterTests
{
    private const string Schema = """
        {
          "type": "object",
          "required": [ "tripId", "year" ],
          "properties": {
            "tripId": { "type": "string", "pattern": "^T-\\d+$", "maxLength": 10 },
            "vessel": { "type": ["string", "null"] },
            "year": { "type": "integer", "minimum": 2000, "maximum": 2100 },
            "gear": { "enum": [ "OTB", "PTM" ] },
            "hauls": {
              "type": "array",
              "items": {
                "type": "object",
                "required": [ "haulNo" ],
                "properties": { "haulNo": { "type": "integer" }, "weight": { "type": "number" } }
              }
            }
          }
        }
        """;

    [Test]
    public void Json_schema_becomes_shape_and_native_rules()
    {
        var ruleset = JsonSchemaImporter.Import(Schema, "trips-from-jsonschema", "Trip");

        Assert.Multiple(() =>
        {
            Assert.That(ruleset.Shape.Name, Is.EqualTo("Trip"));
            Assert.That(ruleset.Shape.Fields.Select(f => f.Name),
                Is.EquivalentTo(new[] { "tripId", "vessel", "year", "gear" }));
            Assert.That(ruleset.Shape.Children.Single().Name, Is.EqualTo("hauls"));

            Assert.That(ruleset.Rules.Any(r => r is { Field: "tripId", Check: RequiredCheck }));
            Assert.That(ruleset.Rules.Any(r => r is { Field: "year", Check: RangeCheck { Min: 2000, Max: 2100 } }));
            Assert.That(ruleset.Rules.Any(r => r is { Field: "gear", Check: InListCheck { Values.Count: 2 } }));
            Assert.That(ruleset.Rules.Any(r => r.Entity == "hauls" && r is { Field: "haulNo", Check: RequiredCheck }));
            Assert.That(ruleset.Rules.Any(r => r.Entity == "hauls" && r is { Field: "weight", Check: TypeCheck { Kind: DataKind.Decimal } }));
            Assert.That(ruleset.Validate(), Is.Empty);
        });
    }
}
