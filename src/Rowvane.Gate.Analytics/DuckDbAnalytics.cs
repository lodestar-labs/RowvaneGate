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
        using var connection = await OpenWithStagedDataAsync(filePath, format, cancellationToken);
        GuardSandbox(check);
        return await ExecuteCheckAsync(connection, check, filePath, cancellationToken);
    }

    /// <summary>
    /// The batched path the engine uses: the staged file is materialized (and the sandbox
    /// locked) once, and every check runs against that one <c>data</c> table — N rules no
    /// longer mean N full parses of the file. Each check runs inside a rolled-back
    /// transaction so one rule's query cannot mutate what the next rule sees, preserving
    /// the isolation the connection-per-check path had.
    /// </summary>
    public async Task<IReadOnlyList<SqlCheckOutcome>> RunSqlChecksAsync(
        string filePath,
        string format,
        RulesetDocument ruleset,
        IReadOnlyList<SqlCheck> checks,
        CancellationToken cancellationToken = default)
    {
        if (checks.Count == 0)
        {
            return [];
        }

        using var connection = await OpenWithStagedDataAsync(filePath, format, cancellationToken);
        var outcomes = new List<SqlCheckOutcome>(checks.Count);
        foreach (var check in checks)
        {
            try
            {
                GuardSandbox(check);
                await ExecuteStatementAsync(connection, "BEGIN TRANSACTION;", cancellationToken);
                try
                {
                    outcomes.Add(new SqlCheckOutcome(
                        await ExecuteCheckAsync(connection, check, filePath, cancellationToken), null));
                }
                finally
                {
                    try
                    {
                        await ExecuteStatementAsync(connection, "ROLLBACK;", CancellationToken.None);
                    }
                    catch
                    {
                        // No transaction left to roll back (the query committed or aborted it).
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcomes.Add(new SqlCheckOutcome(null, ex.Message));
            }
        }

        return outcomes;
    }

    /// <summary>
    /// Opens an in-memory connection, materializes the staged file as the <c>data</c>
    /// table, and — unless unsandboxed rules are allowed — disables external access so a
    /// rule's query can read the table but no other file on the host.
    /// </summary>
    private async Task<DuckDBConnection> OpenWithStagedDataAsync(
        string filePath, string format, CancellationToken cancellationToken)
    {
        var readFunction = ReadFunctionFor(format)
            ?? throw new NotSupportedException(
                $"SQL rules read the staged file with DuckDB, which supports csv, json, and parquet — not '{format}'.");

        var connection = new DuckDBConnection("DataSource=:memory:");
        try
        {
            await connection.OpenAsync(cancellationToken);
            await ExecuteStatementAsync(
                connection,
                $"CREATE TABLE data AS SELECT * FROM {readFunction}({Quote(filePath)});",
                cancellationToken);

            if (!allowUnsandboxedSqlRules)
            {
                await ExecuteStatementAsync(connection, "SET enable_external_access = false;", cancellationToken);
            }

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void GuardSandbox(SqlCheck check)
    {
        if (!allowUnsandboxedSqlRules && check.Query.Contains("{file}", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "The {file} placeholder needs direct file access, which sandboxed SQL rules do not allow. " +
                "Query the staged 'data' table instead, or enable Gate:AllowUnsandboxedSqlRules.");
        }
    }

    private static async Task ExecuteStatementAsync(
        DuckDBConnection connection, string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> ExecuteCheckAsync(
        DuckDBConnection connection, SqlCheck check, string filePath, CancellationToken cancellationToken)
    {
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
