using System.Text;
using DuckDB.NET.Data;
using Rowvane.Gate.Rulesets;
using Rowvane.Gate.Sources;

namespace Rowvane.Gate.Analytics;

/// <summary>
/// Analytical tier backed by embedded DuckDB. Two capabilities:
/// <para><b>SQL rules</b> — a rule's query runs against the staged file (exposed as the
/// table <c>data</c>); every returned row is a violation, its columns formatted into the
/// finding message. The escape hatch for any dataset-level check the declarative rules
/// can't express. By default queries run sandboxed: the file is materialized first and
/// DuckDB's external access is disabled, so a query can never touch other host files.
/// The <c>{file}</c> placeholder (direct file access with custom read options) requires
/// opting out of the sandbox.</para>
/// <para><b>Profiling</b> — column statistics (<c>SUMMARIZE</c>) for a quick picture of
/// an unfamiliar file before writing rules for it.</para>
/// DuckDB reads csv/json/parquet natively; XML files are not supported by this tier.
/// </summary>
public sealed class DuckDbAnalytics(bool allowUnsandboxedSqlRules = false) : IAnalyticsRunner
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

        using (var stage = connection.CreateCommand())
        {
            // Materialize the staged file, then lock the sandbox: with external access off,
            // a rule's query can read the 'data' table but no other file on the host.
            stage.CommandText = $"CREATE TABLE data AS SELECT * FROM {readFunction}({Quote(filePath)});";
            await stage.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!allowUnsandboxedSqlRules)
        {
            if (check.Query.Contains("{file}", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    "The {file} placeholder needs direct file access, which sandboxed SQL rules do not allow. " +
                    "Query the staged 'data' table instead, or enable Gate:AllowUnsandboxedSqlRules.");
            }

            using var lockdown = connection.CreateCommand();
            lockdown.CommandText = "SET enable_external_access = false;";
            await lockdown.ExecuteNonQueryAsync(cancellationToken);
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
