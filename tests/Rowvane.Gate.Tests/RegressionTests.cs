using Rowvane.Gate.Readers.Csv;
using Rowvane.Gate.Rulesets;
using Rowvane.Gate.Sources;

namespace Rowvane.Gate.Tests;

[TestFixture]
public class RegressionTests
{
    [Test]
    public void Child_belonging_to_a_previous_tree_after_a_new_root_is_a_structural_error()
    {
        // SA's parent entity is HL, but the only HL belongs to trip 1, which was already
        // completed when TR,T-2 began. Attaching to it would silently drop the record
        // from validation (trip 1 has already been yielded and evaluated).
        const string file = """
            TR,T-1,DANA,2026-06-01,100
            HL,1,OTB,10
            TR,T-2,HAVKAT,2026-06-02,200
            SA,COD,1
            """;

        Assert.ThrowsAsync<SourceFormatException>(async () =>
            await TestData.ReadAll(new CsvValidationSource(), file, TestData.Trips()));
    }

    [Test]
    public void Field_level_rule_without_an_entity_is_reported_as_invalid()
    {
        var ruleset = TestData.Trips();
        ruleset.Rules.Add(new Rule { Field = "TripId", Check = new RequiredCheck() });

        // A field check with no entity never matches any record: the rule would be a
        // silent no-op unless structural validation rejects it.
        Assert.That(ruleset.Validate(), Has.Some.Contains("entity"));
    }

    [Test]
    public void Json_schema_draft4_boolean_exclusive_bounds_are_honored()
    {
        const string schema = """
            {
              "type": "object",
              "properties": {
                "weight": { "type": "number", "minimum": 0, "exclusiveMinimum": true, "maximum": 100 }
              }
            }
            """;

        var ruleset = Schemas.Json.JsonSchemaImporter.Import(schema, "draft4");
        var range = (RangeCheck)ruleset.Rules.Single(r => r.Check is RangeCheck).Check;

        Assert.Multiple(() =>
        {
            Assert.That(range.Min, Is.EqualTo(0));
            Assert.That(range.ExclusiveMin, Is.True, "draft-4 boolean exclusiveMinimum");
            Assert.That(range.Max, Is.EqualTo(100));
            Assert.That(range.ExclusiveMax, Is.False);
        });
    }

    [Test]
    public async Task Csv_parser_handles_escaped_quotes_embedded_delimiters_and_newlines()
    {
        const string file = "a,\"x\"\"y\",\"line1\r\nline2\",end\r\nnext,row,three,4\r\n";
        var rows = new List<CsvParser.CsvRow>();
        await foreach (var row in CsvParser.ParseAsync(new StringReader(file)))
        {
            rows.Add(row);
        }

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows[0].Fields, Is.EqualTo(new[] { "a", "x\"y", "line1\nline2", "end" }));
            Assert.That(rows[0].LineNumber, Is.EqualTo(1));
            Assert.That(rows[1].LineNumber, Is.EqualTo(3), "row 1 spans two physical lines");
        });
    }

    [Test]
    public async Task Line_numbers_stay_correct_past_the_delimiter_sniffing_window()
    {
        // The sniffer samples the first 20 lines and replays them; rows read after the
        // sample must still report their true physical line numbers.
        var ruleset = TestData.Trips();
        ruleset.Shape.Children.Clear();
        ruleset.Source.Csv.Mode = CsvMode.Single;
        ruleset.Source.Csv.HasHeaderRow = false;

        var file = string.Concat(Enumerable.Range(1, 25).Select(i => $"RT,T-{i},V,2026-01-01,{i}\n"));
        var roots = await TestData.ReadAll(new CsvValidationSource(), file, ruleset);

        Assert.Multiple(() =>
        {
            Assert.That(roots, Has.Count.EqualTo(25));
            Assert.That(roots[24].Location.Line, Is.EqualTo(25));
            Assert.That(roots[24].Get("TripId"), Is.EqualTo("T-25"));
        });
    }

    /// <summary>TR → HL → SA → SD, four levels — deep enough to expose stale-branch bugs.</summary>
    private static RulesetDocument FourLevels()
    {
        var ruleset = TestData.Trips();
        var sample = ruleset.Shape.Children[0].Children[0];
        sample.Children.Add(new EntityShape
        {
            Name = "SD",
            Fields = [new FieldShape { Name = "RecordType" }, new FieldShape { Name = "DetailNo" }],
        });
        return ruleset;
    }

    [Test]
    public void Record_after_a_new_sibling_branch_cannot_attach_to_the_previous_branch()
    {
        // SD's parent entity is SA, but the only SA belongs to haul 1 — a *sibling branch*
        // already closed when HL,2 began. Attaching there would validate SD against the
        // wrong parents (wrong parent.* comparisons and aggregates) with no error at all.
        const string file = """
            TR,T-1,DANA,2026-06-01,100
            HL,1,OTB,10
            SA,COD,1
            HL,2,PTM,20
            SD,1
            """;

        Assert.ThrowsAsync<SourceFormatException>(async () =>
            await TestData.ReadAll(new CsvValidationSource(), file, FourLevels()));
    }

    [Test]
    public async Task Four_level_hierarchies_assemble_correctly()
    {
        const string file = """
            TR,T-1,DANA,2026-06-01,100
            HL,1,OTB,10
            SA,COD,1
            SD,1
            HL,2,PTM,20
            SA,HER,2
            SD,2
            """;

        var roots = await TestData.ReadAll(new CsvValidationSource(), file, FourLevels());

        Assert.Multiple(() =>
        {
            Assert.That(roots, Has.Count.EqualTo(1));
            var hauls = roots[0].Children;
            Assert.That(hauls, Has.Count.EqualTo(2));
            Assert.That(hauls[0].Children.Single().Children.Single().Get("DetailNo"), Is.EqualTo("1"));
            Assert.That(hauls[1].Children.Single().Children.Single().Get("DetailNo"), Is.EqualTo("2"));
        });
    }

    [Test]
    public void Invalid_regex_pattern_is_rejected_at_registration()
    {
        var ruleset = TestData.Trips();
        ruleset.Rules.Add(new Rule
        {
            Entity = "TR",
            Field = "TripId",
            Check = new RegexCheck { Pattern = "(" },
        });

        // A pattern .NET cannot compile must fail once, here — not as a 500 on every
        // validation that touches the ruleset afterwards.
        Assert.That(ruleset.Validate(), Has.Some.Contains("regex"));
    }

    [Test]
    public void Fixed_width_start_and_length_must_be_at_least_one()
    {
        var ruleset = TestData.Trips();
        ruleset.Shape.Fields[1].Start = 0;
        ruleset.Shape.Fields[1].Length = 0;

        var errors = ruleset.Validate();

        Assert.Multiple(() =>
        {
            Assert.That(errors, Has.Some.Contains("'start' is 1-based"));
            Assert.That(errors, Has.Some.Contains("'length' must be at least 1"));
        });
    }
}
