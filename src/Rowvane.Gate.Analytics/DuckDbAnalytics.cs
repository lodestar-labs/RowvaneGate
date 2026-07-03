using System.Text;
using DuckDB.NET.Data;
using Rowvane.Gate.Rulesets;
using Rowvane.Gate.Sources;

namespace Rowvane.Gate.Analytics;

/// <summary>
/// Analytical tier backed by embedded DuckDB. Two capabilities:
/// <para><b>SQL rules</b> — a rule's query runs against the staged file (exposed as the
/// view <c>data</c>, or via the <c>{file}</c> placeholder); every returned row is a
/// violation, its columns formatted into the finding message. The escape hatch for any
/// dataset-level check the declarative rules can't express.</para>
/// <para><b>Profiling</b> — column statistics (<c>SUMMARIZE</c>) for a quick picture of
/// an unfamiliar file before writing rules for it.</para>
/// DuckDB reads csv/json/parquet natively; XML files are not supported by this tier.
/// </summary>
public sealed class DuckDbAnalytics : IAnalyticsRunner
{
    private const int MaxViolationRows = 10_000;

    public async Task<IReadOnlyList<string>> RunSqlCheckAsync(
        string filePath,
        string format,
        RulesetDocument ruleset,
        SqlCheck check,
        CancellationToken cancellationToken = default)
    {
        var readFunction = ReadFunctionFor(format)
            ?? throw new NotSupportedException(
                $"SQL rules read the staged file with DuckDB, which supports csv, json, and parquet — not '{format}'.");

        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);

        using (var createView = connection.CreateCommand())
        {
            createView.CommandText = $"CREATE VIEW data AS SELECT * FROM {readFunction}({Quote(filePath)});";
            await createView.ExecuteNonQueryAsync(cancellationToken);
        }

        using var command = connection.CreateCommand();
        command.CommandText = check.Query.Replace("{file}", Quote(filePath), StringComparison.OrdinalIgnoreCase);

        var violations = new List<string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (violations.Count < MaxViolationRows && await reader.ReadAsync(cancellationToken))
        {
            var message = new StringBuilder();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (i > 0)
                {
                    message.Append("; ");
                }

                message.Append(reader.GetName(i)).Append('=');
                message.Append(reader.IsDBNull(i) ? "null" : reader.GetValue(i));
            }

            violations.Add(message.ToString());
        }

        return violations;
    }

    /// <summary>Column statistics for the file — one row per column, SUMMARIZE's shape.</summary>
    public async Task<IReadOnlyList<Dictionary<string, object?>>> ProfileAsync(
        string filePath,
        string format,
        CancellationToken cancellationToken = default)
    {
        var readFunction = ReadFunctionFor(format)
            ?? throw new NotSupportedException($"Profiling supports csv, json, and parquet — not '{format}'.");

        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = $"SUMMARIZE SELECT * FROM {readFunction}({Quote(filePath)});";

        var rows = new List<Dictionary<string, object?>>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static string? ReadFunctionFor(string format) => format.ToLowerInvariant() switch
    {
        "csv" => "read_csv_auto",
        "json" => "read_json_auto",
        "parquet" => "read_parquet",
        _ => null,
    };

    private static string Quote(string path) => $"'{path.Replace("'", "''")}'";
}
