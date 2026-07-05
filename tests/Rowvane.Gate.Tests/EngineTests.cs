using Microsoft.Extensions.Logging.Abstractions;
using Rowvane.Gate.Engine;
using Rowvane.Gate.Findings;
using Rowvane.Gate.Readers.Csv;
using Rowvane.Gate.Rulesets;
using Rowvane.Gate.Sources;

namespace Rowvane.Gate.Tests;

[TestFixture]
public class EngineTests
{
    private static ValidationEngine Engine() => new(
        [new CsvValidationSource()],
        [],
        NullLogger<ValidationEngine>.Instance);

    private static Task<ValidationReport> Validate(string file, RulesetDocument ruleset)
    {
        ruleset.AssignRuleIds();
        return Engine().ValidateAsync(TestData.AsStream(file), "csv", ruleset, "test.csv");
    }

    [Test]
    public async Task Field_rules_produce_findings_with_locations_and_raw_values()
    {
        var ruleset = TestData.Trips();
        const string file = """
            TR,,DANA,2026-06-01,100
            HL,1,OTB,not-a-number
            """;

        var report = await Validate(file, ruleset);

        Assert.Multiple(() =>
        {
            Assert.That(report.Valid, Is.False);
            Assert.That(report.ErrorCount, Is.EqualTo(2));
            var required = report.Findings.Single(f => f.RuleId == "TRIP-ID");
            Assert.That(required.Line, Is.EqualTo(1));
            var type = report.Findings.Single(f => f.RuleId == "WEIGHT-NUM");
            Assert.That(type.RawValue, Is.EqualTo("not-a-number"));
            Assert.That(type.Line, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Range_regex_list_length_and_condition_all_apply()
    {
        var ruleset = TestData.Trips();
        ruleset.Rules.AddRange(
        [
            new Rule { Id = "W-RANGE", Entity = "HL", Field = "Weight", Check = new RangeCheck { Min = 0, Max = 500 } },
            new Rule { Id = "GEAR-LIST", Entity = "HL", Field = "Gear", Check = new InListCheck { Values = ["OTB", "PTM"] } },
            new Rule { Id = "TRIP-FMT", Entity = "TR", Field = "TripId", Check = new RegexCheck { Pattern = "^T-\\d+$" } },
            new Rule { Id = "V-LEN", Entity = "TR", Field = "Vessel", Check = new LengthCheck { Max = 4 } },
            new Rule
            {
                Id = "OTB-ONLY",
                Entity = "HL",
                Field = "Weight",
                Check = new RangeCheck { Max = 100 },
                When = new RuleCondition { Field = "Gear", OneOf = ["OTB"] },
            },
        ]);

        const string file = """
            TR,BAD1,LONGNAME,2026-06-01,100
            HL,1,XXX,900
            HL,2,OTB,150
            """;

        var report = await Validate(file, ruleset);
        var byRule = report.Findings.GroupBy(f => f.RuleId).ToDictionary(g => g.Key, g => g.Count());

        Assert.Multiple(() =>
        {
            Assert.That(byRule["W-RANGE"], Is.EqualTo(1), "900 exceeds range");
            Assert.That(byRule["GEAR-LIST"], Is.EqualTo(1), "XXX not allowed");
            Assert.That(byRule["TRIP-FMT"], Is.EqualTo(1));
            Assert.That(byRule["V-LEN"], Is.EqualTo(1));
            Assert.That(byRule["OTB-ONLY"], Is.EqualTo(1), "conditional fires only for OTB haul (150 > 100)");
        });
    }

    [Test]
    public async Task Compare_against_parent_field_works_across_levels()
    {
        var ruleset = TestData.Trips();
        ruleset.Rules.Add(new Rule
        {
            Id = "HAUL-LE-TRIP",
            Entity = "HL",
            Field = "Weight",
            Check = new CompareCheck { Op = CompareOp.Le, OtherField = "parent.TotalWeight" },
        });

        const string file = """
            TR,T-1,DANA,2026-06-01,1000
            HL,1,OTB,800
            HL,2,OTB,1200
            """;

        var report = await Validate(file, ruleset);
        var finding = report.Findings.Single(f => f.RuleId == "HAUL-LE-TRIP");

        Assert.Multiple(() =>
        {
            Assert.That(finding.Line, Is.EqualTo(3), "only the 1200 haul violates");
            Assert.That(finding.Message, Does.Contain("1200"));
        });
    }

    [Test]
    public async Task Uniqueness_dataset_and_parent_scoped()
    {
        var ruleset = TestData.Trips();
        ruleset.Rules.AddRange(
        [
            new Rule { Id = "TRIP-UNIQ", Entity = "TR", Check = new UniqueCheck { Fields = ["TripId"] } },
            new Rule { Id = "HAUL-UNIQ", Entity = "HL", Check = new UniqueCheck { Fields = ["HaulNo"], Scope = UniqueScope.Parent } },
        ]);

        const string file = """
            TR,T-1,DANA,2026-06-01,100
            HL,1,OTB,10
            HL,1,PTM,20
            TR,T-1,HAVKAT,2026-06-02,200
            HL,1,OTB,30
            """;

        var report = await Validate(file, ruleset);

        Assert.Multiple(() =>
        {
            Assert.That(report.Findings.Count(f => f.RuleId == "TRIP-UNIQ"), Is.EqualTo(1), "second T-1 flagged");
            Assert.That(report.Findings.Count(f => f.RuleId == "HAUL-UNIQ"), Is.EqualTo(1),
                "duplicate HaulNo within trip 1 only; trip 2's HaulNo 1 is fine");
        });
    }

    [Test]
    public async Task Aggregate_deviation_between_parent_and_children()
    {
        var ruleset = TestData.Trips();
        ruleset.Rules.Add(new Rule
        {
            Id = "SUM-DEV",
            Entity = "TR",
            Field = "TotalWeight",
            Check = new AggregateCheck
            {
                ChildEntity = "HL",
                ChildField = "Weight",
                Function = AggregateFunction.Sum,
                DeviationPercent = 5,
            },
        });

        const string file = """
            TR,T-1,DANA,2026-06-01,1000
            HL,1,OTB,600
            HL,2,OTB,410
            TR,T-2,HAVKAT,2026-06-02,1000
            HL,1,OTB,600
            HL,2,OTB,300
            """;

        var report = await Validate(file, ruleset);
        var finding = report.Findings.Single(f => f.RuleId == "SUM-DEV");

        Assert.Multiple(() =>
        {
            Assert.That(finding.Path, Is.EqualTo("TR"));
            Assert.That(finding.Line, Is.EqualTo(4), "trip 2: sum 900 deviates 10% from 1000; trip 1's 1010 is within 5%");
        });
    }

    [Test]
    public async Task Aggregate_deviation_against_a_zero_parent_flags_any_nonzero_aggregate()
    {
        var ruleset = TestData.Trips();
        ruleset.Rules.Add(new Rule
        {
            Id = "SUM-DEV",
            Entity = "TR",
            Field = "TotalWeight",
            Check = new AggregateCheck
            {
                ChildEntity = "HL",
                ChildField = "Weight",
                Function = AggregateFunction.Sum,
                DeviationPercent = 5,
            },
        });

        // Percent deviation is undefined against 0: a zero parent with nonzero children
        // is a violation; a zero parent with zero children is not.
        const string file = """
            TR,T-1,DANA,2026-06-01,0
            HL,1,OTB,10
            TR,T-2,HAVKAT,2026-06-02,0
            HL,1,OTB,0
            """;

        var report = await Validate(file, ruleset);
        var finding = report.Findings.Single(f => f.RuleId == "SUM-DEV");

        Assert.That(finding.Line, Is.EqualTo(1), "only the trip whose children sum to a nonzero value");
    }

    [Test]
    public async Task Required_children_and_row_counts()
    {
        var ruleset = TestData.Trips();
        ruleset.Rules.Add(new Rule
        {
            Id = "MAX-TRIPS",
            Entity = "TR",
            Check = new RowCountCheck { Max = 1 },
        });

        const string file = """
            TR,T-1,DANA,2026-06-01,100
            TR,T-2,HAVKAT,2026-06-02,200
            HL,1,OTB,10
            """;

        var report = await Validate(file, ruleset);

        Assert.Multiple(() =>
        {
            Assert.That(report.Findings.Count(f => f.RuleId == "SHAPE"), Is.EqualTo(1), "trip 1 has no hauls");
            Assert.That(report.Findings.Count(f => f.RuleId == "MAX-TRIPS"), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Finding_caps_truncate_lists_but_not_counts()
    {
        var ruleset = TestData.Trips();
        ruleset.MaxFindingsPerRule = 3;
        var lines = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"TR,,V{i},2026-01-01,1\nHL,1,OTB,1"));

        var report = await Validate(lines, ruleset);

        Assert.Multiple(() =>
        {
            Assert.That(report.Findings.Count(f => f.RuleId == "TRIP-ID"), Is.EqualTo(3));
            Assert.That(report.FindingsByRule["TRIP-ID"], Is.EqualTo(10));
            Assert.That(report.ErrorCount, Is.EqualTo(10));
        });
    }

    [Test]
    public async Task Sql_rules_without_analytics_surface_as_warnings()
    {
        var ruleset = TestData.Trips();
        ruleset.Rules.Add(new Rule
        {
            Id = "SQL-1",
            Entity = "TR",
            Check = new SqlCheck { Query = "SELECT 1" },
        });

        var report = await Validate("TR,T-1,DANA,2026-06-01,1\nHL,1,OTB,1", ruleset);
        var finding = report.Findings.Single(f => f.RuleId == "SQL-1");

        Assert.Multiple(() =>
        {
            Assert.That(finding.Severity, Is.EqualTo(Severity.Warning));
            Assert.That(finding.Message, Does.Contain("analytics engine is not enabled"));
        });
    }

    [Test]
    public async Task Record_counts_land_in_the_report()
    {
        var report = await Validate("TR,T-1,DANA,2026-06-01,1\nHL,1,OTB,1\nSA,COD,1", TestData.Trips());

        Assert.Multiple(() =>
        {
            Assert.That(report.RecordsRead, Is.EqualTo(3));
            Assert.That(report.RecordCounts["HL"], Is.EqualTo(1));
            Assert.That(report.Valid, Is.True);
        });
    }
}
