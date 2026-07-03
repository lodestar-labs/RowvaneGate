using Rowvane.Gate.Readers.Csv;
using Rowvane.Gate.Readers.Json;
using Rowvane.Gate.Readers.Xml;
using Rowvane.Gate.Rulesets;
using Rowvane.Gate.Sources;

namespace Rowvane.Gate.Tests;

[TestFixture]
public class MultiRecordCsvTests
{
    private const string File = """
        TR,T-001,DANA,2026-06-01,1500
        HL,1,OTB,800
        SA,COD,1
        SA,HER,2
        HL,2,PTM,700
        SA,SPR,1
        TR,T-002,HAVKAT,2026-06-02,900
        HL,1,OTB,900
        """;

    [Test]
    public async Task Reconstructs_hierarchy_from_record_order()
    {
        var roots = await TestData.ReadAll(new CsvValidationSource(), File, TestData.Trips());

        Assert.Multiple(() =>
        {
            Assert.That(roots, Has.Count.EqualTo(2));
            Assert.That(roots[0].Get("TripId"), Is.EqualTo("T-001"));
            Assert.That(roots[0].Children, Has.Count.EqualTo(2), "two hauls under trip 1");
            Assert.That(roots[0].Children[0].Children, Has.Count.EqualTo(2), "two samples under haul 1");
            Assert.That(roots[0].Children[1].Children.Single().Get("Species"), Is.EqualTo("SPR"));
            Assert.That(roots[1].Children, Has.Count.EqualTo(1));
            Assert.That(roots[1].Children[0].Parent, Is.SameAs(roots[1]), "parent links are set");
        });
    }

    [Test]
    public async Task Line_numbers_point_at_the_source()
    {
        var roots = await TestData.ReadAll(new CsvValidationSource(), File, TestData.Trips());

        Assert.Multiple(() =>
        {
            Assert.That(roots[0].Location.Line, Is.EqualTo(1));
            Assert.That(roots[0].Children[1].Location.Line, Is.EqualTo(5));
            Assert.That(roots[1].Location.Line, Is.EqualTo(7));
        });
    }

    [Test]
    public void Child_before_parent_is_a_structural_error()
    {
        Assert.ThrowsAsync<SourceFormatException>(async () =>
            await TestData.ReadAll(new CsvValidationSource(), "HL,1,OTB,10\n", TestData.Trips()));
    }

    [Test]
    public void Unknown_record_type_fails_unless_ignored()
    {
        const string file = "TR,T-1,V,2026-01-01,10\nZZ,strange\n";
        Assert.ThrowsAsync<SourceFormatException>(async () =>
            await TestData.ReadAll(new CsvValidationSource(), file, TestData.Trips()));
    }

    [Test]
    public async Task Unknown_record_type_is_skipped_when_opted_in()
    {
        var ruleset = TestData.Trips();
        ruleset.Source.Csv.IgnoreUnknownRecordTypes = true;
        var roots = await TestData.ReadAll(new CsvValidationSource(), "TR,T-1,V,2026-01-01,10\nZZ,strange\n", ruleset);

        Assert.That(roots, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Semicolon_delimiter_is_sniffed()
    {
        var file = File.Replace(',', ';');
        var roots = await TestData.ReadAll(new CsvValidationSource(), file, TestData.Trips());

        Assert.That(roots[0].Get("Vessel"), Is.EqualTo("DANA"));
    }
}

[TestFixture]
public class SingleCsvTests
{
    private static RulesetDocument Flat()
    {
        var ruleset = TestData.Trips();
        ruleset.Shape.Children.Clear();
        ruleset.Source.Csv.Mode = CsvMode.Single;
        return ruleset;
    }

    [Test]
    public async Task Header_row_is_detected_and_mapped()
    {
        const string file = "TripId,Vessel,DepartureDate\nT-1,DANA,2026-06-01\nT-2,HAVKAT,2026-06-02\n";
        var roots = await TestData.ReadAll(new CsvValidationSource(), file, Flat());

        Assert.Multiple(() =>
        {
            Assert.That(roots, Has.Count.EqualTo(2));
            Assert.That(roots[0].Get("Vessel"), Is.EqualTo("DANA"));
            Assert.That(roots[1].Location.Line, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task Headerless_files_map_positionally()
    {
        var ruleset = Flat();
        ruleset.Source.Csv.HasHeaderRow = false;
        var roots = await TestData.ReadAll(new CsvValidationSource(), "RT,T-1,DANA,2026-06-01,10\n", ruleset);

        Assert.That(roots.Single().Get("TripId"), Is.EqualTo("T-1"));
    }
}

[TestFixture]
public class FixedWidthTests
{
    private static RulesetDocument FixedWidth()
    {
        var ruleset = TestData.Trips();
        ruleset.Source.Csv.Mode = CsvMode.FixedWidth;
        var tr = ruleset.Shape;
        SetPos(tr, "RecordType", 1, 2);
        SetPos(tr, "TripId", 3, 6);
        SetPos(tr, "Vessel", 9, 8);
        SetPos(tr, "DepartureDate", 17, 10);
        SetPos(tr, "TotalWeight", 27, 6);
        var hl = tr.Children[0];
        SetPos(hl, "RecordType", 1, 2);
        SetPos(hl, "HaulNo", 3, 3);
        SetPos(hl, "Gear", 6, 4);
        SetPos(hl, "Weight", 10, 6);
        var sa = hl.Children[0];
        SetPos(sa, "RecordType", 1, 2);
        SetPos(sa, "Species", 3, 4);
        SetPos(sa, "SampleNo", 7, 3);
        return ruleset;

        static void SetPos(EntityShape entity, string field, int start, int length)
        {
            var shape = entity.FindField(field)!;
            shape.Start = start;
            shape.Length = length;
        }
    }

    [Test]
    public async Task Slices_columns_by_position()
    {
        const string file = "TRT-001 DANA    2026-06-01  1500\nHL001OTB   800\nSACOD 001\n";
        var roots = await TestData.ReadAll(new CsvValidationSource(), file, FixedWidth());

        var trip = roots.Single();
        Assert.Multiple(() =>
        {
            Assert.That(trip.Get("TripId"), Is.EqualTo("T-001"));
            Assert.That(trip.Get("Vessel"), Is.EqualTo("DANA"));
            Assert.That(trip.Children.Single().Get("Weight"), Is.EqualTo("800"));
            Assert.That(trip.Children.Single().Children.Single().Get("Species"), Is.EqualTo("COD"));
        });
    }
}

[TestFixture]
public class XmlJsonSourceTests
{
    [Test]
    public async Task Xml_streams_hierarchy_with_line_numbers()
    {
        const string xml = """
            <File>
              <TR>
                <TripId>T-1</TripId>
                <Vessel>DANA</Vessel>
                <HL><HaulNo>1</HaulNo><SA><Species>COD</Species></SA></HL>
              </TR>
            </File>
            """;

        var roots = await TestData.ReadAll(new XmlValidationSource(), xml, TestData.Trips());

        var trip = roots.Single();
        Assert.Multiple(() =>
        {
            Assert.That(trip.Get("TripId"), Is.EqualTo("T-1"));
            Assert.That(trip.Location.Line, Is.EqualTo(2));
            Assert.That(trip.Children.Single().Children.Single().Get("Species"), Is.EqualTo("COD"));
            Assert.That(trip.Children[0].Parent, Is.SameAs(trip));
        });
    }

    [Test]
    public async Task Json_array_streams_with_children()
    {
        const string json = """
            [ { "tripId": "T-1", "hl": [ { "haulNo": 1, "sa": [ { "species": "COD" } ] } ] } ]
            """;

        var roots = await TestData.ReadAll(new JsonValidationSource(), json, TestData.Trips());

        Assert.That(roots.Single().Children.Single().Children.Single().Get("Species"), Is.EqualTo("COD"));
    }
}
