using Rowvane.Gate.Rulesets;

namespace Rowvane.Gate.Tests;

/// <summary>A compact 3-level hierarchy (Trip → Haul → Sample) shared across the suite.</summary>
internal static class TestData
{
    public static RulesetDocument Trips() => new()
    {
        Name = "trips",
        Shape = new EntityShape
        {
            Name = "TR",
            Fields =
            [
                new FieldShape { Name = "RecordType" },
                new FieldShape { Name = "TripId" },
                new FieldShape { Name = "Vessel" },
                new FieldShape { Name = "DepartureDate" },
                new FieldShape { Name = "TotalWeight" },
            ],
            Children =
            [
                new EntityShape
                {
                    Name = "HL",
                    Required = true,
                    Fields =
                    [
                        new FieldShape { Name = "RecordType" },
                        new FieldShape { Name = "HaulNo" },
                        new FieldShape { Name = "Gear" },
                        new FieldShape { Name = "Weight" },
                    ],
                    Children =
                    [
                        new EntityShape
                        {
                            Name = "SA",
                            Fields =
                            [
                                new FieldShape { Name = "RecordType" },
                                new FieldShape { Name = "Species" },
                                new FieldShape { Name = "SampleNo" },
                            ],
                        },
                    ],
                },
            ],
        },
        Source = { Csv = { Mode = CsvMode.MultiRecord } },
        Rules =
        [
            new Rule { Id = "TRIP-ID", Entity = "TR", Field = "TripId", Check = new RequiredCheck() },
            new Rule { Id = "WEIGHT-NUM", Entity = "HL", Field = "Weight", Check = new TypeCheck { Kind = DataKind.Decimal } },
        ],
    };

    public static Stream AsStream(string content) =>
        new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));

    public static async Task<List<Records.ValidationRecord>> ReadAll(
        Sources.IValidationSource source, string content, RulesetDocument ruleset)
    {
        var records = new List<Records.ValidationRecord>();
        await foreach (var record in source.ReadAsync(AsStream(content), ruleset))
        {
            records.Add(record);
        }

        return records;
    }
}
