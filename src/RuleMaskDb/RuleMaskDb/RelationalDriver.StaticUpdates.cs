using System.Data.Common;
using Npgsql;
using RuleMaskDb.ScriptDom;
using RuleMaskDb.SqlDomain;

namespace RuleMaskDb;

public sealed partial class RelationalDriver
{
    /// <summary>
    /// Creates a bounded key page ordered by the complete row key, strictly after the previous page.
    /// Keys must be unique, non-null, orderable, and unchanged throughout execution.
    /// </summary>
    public DbCommand CreateKeyPage(DbConnection connection, TableDescription table,
        IReadOnlyList<string> keys, int batchSize, IReadOnlyList<object?>? lastKey)
    {
        if (keys.Count == 0 || (lastKey is not null && lastKey.Count != keys.Count))
            throw new ArgumentException("A key page requires keys and a matching continuation key.");
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));

        var command = connection.CreateCommand();
        try
        {
            AddParameter(command, "pageSize", batchSize);
            var columns = string.Join(", ", keys.Select(QuoteIdentifier));
            var filter = lastKey is null ? "" : " WHERE " + CreateKeyContinuation(command, keys, lastKey);
            var source = $"FROM {QuoteTable(table)}{filter} ORDER BY {columns}";
            command.CommandText = databaseType switch
            {
                DatabaseType.SqlServer => $"SELECT TOP (@pageSize) {columns} {source}",
                DatabaseType.PostgreSQL => $"SELECT {columns} {source} LIMIT @pageSize",
                _ => throw new NotSupportedException($"Unsupported database type: {databaseType}")
            };
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    private string CreateKeyContinuation(DbCommand command, IReadOnlyList<string> keys, IReadOnlyList<object?> lastKey)
    {
        var alternatives = new List<string>();
        var equalPrefix = new List<string>();
        for (var i = 0; i < keys.Count; i++)
        {
            if (lastKey[i] is null or DBNull)
                throw new ArgumentException("Continuation keys must not contain NULL.");

            var parameter = AddParameter(command, $"last{i}", lastKey[i]);
            var column = QuoteIdentifier(keys[i]);
            alternatives.Add("(" + string.Join(" AND ", equalPrefix.Append($"{column} > {parameter}")) + ")");
            equalPrefix.Add($"{column} = {parameter}");
        }

        // Evaluate composite ordering in the database, including its collation semantics.
        return string.Join(" OR ", alternatives);
    }

    /// <summary>
    /// Creates a table-wide static update for tables without a reliable pagination key.
    /// </summary>
    public DbCommand CreateStaticUpdate(DbConnection connection, TableDescription table,
        IReadOnlyList<string> columns, IReadOnlyList<object?> values)
    {
        var command = connection.CreateCommand();
        try
        {
            var assignments = AddStaticAssignments(command, table, columns, values);
            command.CommandText = $"UPDATE {QuoteTable(table)} SET {assignments}";
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Updates exactly the selected key page, sending each static value once per command.
    /// Returns a row index for each updated key, independently of trigger row counts.
    /// </summary>
    public DbCommand CreateStaticUpdateBatch(DbConnection connection, TableDescription table,
        IReadOnlyList<string> keys, IReadOnlyList<string> columns,
        IReadOnlyList<object?[]> rows, IReadOnlyList<object?> values)
    {
        if (keys.Count == 0 || rows.Count == 0 || rows.Any(row => row.Length != keys.Count || row.Any(v => v is null or DBNull)))
            throw new ArgumentException("A static update batch requires non-null row keys of matching width.");
        if (GetEffectiveBatchSize(rows.Count, keys.Count, columns.Count) != rows.Count)
            throw new ArgumentException("Static update batch exceeds the parameter budget.");

        var command = connection.CreateCommand();
        try
        {
            var assignments = AddStaticAssignments(command, table, columns, values);
            var source = CreateStaticKeySource(command, keys.Count, rows);
            command.CommandText = CreateJoinedUpdateSql(table, keys, assignments, source);
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    private string CreateStaticKeySource(DbCommand command, int keyCount, IReadOnlyList<object?[]> rows)
    {
        var tuples = new List<string>();
        for (var row = 0; row < rows.Count; row++)
        {
            var parameters = new List<string> { row.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            for (var key = 0; key < keyCount; key++)
                parameters.Add(AddParameter(command, $"key{row}_{key}", rows[row][key]));

            tuples.Add("(" + string.Join(", ", parameters) + ")");
        }

        var aliases = new[] { "row_index" }.Concat(Enumerable.Range(0, keyCount).Select(i => $"c{i}"));
        return $"(VALUES {string.Join(", ", tuples)}) AS s ({string.Join(", ", aliases.Select(QuoteIdentifier))})";
    }

    private string AddStaticAssignments(DbCommand command, TableDescription table,
        IReadOnlyList<string> columns, IReadOnlyList<object?> values)
    {
        if (columns.Count == 0 || columns.Count != values.Count)
            throw new ArgumentException("A static update requires columns and an equally sized list of values.");
        GetEffectiveBatchSize(1, columns.Count);

        var assignments = new List<string>();
        for (var i = 0; i < columns.Count; i++)
        {
            var parameterName = AddParameter(command, $"p{i}", values[i]);
            // Direct SET parameters let PostgreSQL infer NULL's type from the target column.
            if (command.Parameters[i] is NpgsqlParameter { Value: DBNull } parameter)
                parameter.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Unknown;

            assignments.Add($"{QuoteIdentifier(columns[i])} = {FitColumnValue(table, columns[i], parameterName)}");
        }

        return string.Join(", ", assignments);
    }

    private static string AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
        return "@" + name;
    }
}
