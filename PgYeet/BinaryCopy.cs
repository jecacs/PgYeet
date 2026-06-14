using Npgsql;

namespace PgYeet;

/// <summary>
/// Low-level binary COPY runner. Each column is a precompiled writer that knows how to pull its
/// value from the row and write it with the correct Postgres type (built by <see cref="EfModel"/>).
/// </summary>
public static class BinaryCopy
{
    /// <summary>
    /// Streams <paramref name="rows"/> into the table named by <paramref name="copyCommand"/> using
    /// the supplied per-column <paramref name="writers"/>. Returns the number of rows written.
    /// </summary>
    public static async Task<ulong> WriteAsync<T>(
        NpgsqlConnection connection,
        string copyCommand,
        IReadOnlyList<Func<NpgsqlBinaryImporter, T, CancellationToken, ValueTask>> writers,
        IEnumerable<T> rows,
        CancellationToken ct = default)
    {
        await using var importer = await connection.BeginBinaryImportAsync(copyCommand, ct);
        foreach (var row in rows)
        {
            await importer.StartRowAsync(ct);
            for (var i = 0; i < writers.Count; i++)
                await writers[i](importer, row, ct);
        }
        return await importer.CompleteAsync(ct);
    }
}
