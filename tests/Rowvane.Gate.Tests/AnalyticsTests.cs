using Rowvane.Gate.Analytics;
using Rowvane.Gate.Rulesets;

namespace Rowvane.Gate.Tests;

/// <summary>Exercises the embedded DuckDB engine for real — no mocks.</summary>
[TestFixture]
public class AnalyticsTests
{
    private string _csvPath = null!;

    [OneTimeSetUp]
    public void CreateSampleFile()
    {
        _csvPath = Path.Combine(Path.GetTempPath(), $"gate-analytics-{Guid.NewGuid():n}.csv");
        File.WriteAllText(_csvPath, """
            TripId,Vessel,Weight
            T-1,DANA,100
            T-2,DANA,250
            T-2,HAVKAT,300
            T-3,SKADEN,-5
            """);
    }

    [OneTimeTearDown]
    public void DeleteSampleFile() => File.Delete(_csvPath);

    [Test]
    public async Task Sql_rule_returns_one_message_per_violating_row()
    {
        var analytics = new DuckDbAnalytics();
        var check = new SqlCheck
        {
            Query = "SELECT TripId, COUNT(*) AS occurrences FROM data GROUP BY TripId HAVING COUNT(*) > 1",
        };

        var violations = await analytics.RunSqlCheckAsync(
            _csvPath, "csv", TestData.Trips(), check);

        Assert.Multiple(() =>
        {
            Assert.That(violations, Has.Count.EqualTo(1));
            Assert.That(violations[0], Does.Contain("TripId=T-2").And.Contain("occurrences=2"));
        });
    }

    [Test]
    public async Task Profile_summarizes_columns()
    {
        var analytics = new DuckDbAnalytics();
        var profile = await analytics.ProfileAsync(_csvPath, "csv");

        Assert.Multiple(() =>
        {
            Assert.That(profile, Has.Count.EqualTo(3), "one row per column");
            var weight = profile.Single(p => Equals(p["column_name"], "Weight"));
            Assert.That(weight["min"]?.ToString(), Is.EqualTo("-5"));
            Assert.That(weight["max"]?.ToString(), Is.EqualTo("300"));
        });
    }

    [Test]
    public void Xml_is_rejected_with_a_clear_message()
    {
        var analytics = new DuckDbAnalytics();

        Assert.ThrowsAsync<NotSupportedException>(() =>
            analytics.RunSqlCheckAsync(_csvPath, "xml", TestData.Trips(), new SqlCheck { Query = "SELECT 1" }));
    }
}
