using System.Data.Common;

namespace RuleMaskDb;

/// <summary>
/// Verifies that each submitted key matches exactly one row, including trigger-suppressed updates.
/// </summary>
internal static class BatchUpdateExecutor
{
    public static async Task ExecuteAsync(DbCommand command, string tableName, int expectedRows, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var seen = new HashSet<int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var index = reader.GetInt32(0);
            if (index < 0 || index >= expectedRows || !seen.Add(index))
                throw new InvalidOperationException($"Expected one updated row per batch entry in {tableName}.");
        }

        // Consume completion/errors before accepting OUTPUT/RETURNING results.
        while (await reader.NextResultAsync(cancellationToken))
        while (await reader.ReadAsync(cancellationToken)) { }

        if (seen.Count != expectedRows)
            throw new InvalidOperationException($"Expected {expectedRows} updated rows in {tableName}, got {seen.Count}.");
    }
}
