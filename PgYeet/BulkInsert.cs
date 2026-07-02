using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace PgYeet;

internal static class BulkInsert
{
    public static async Task<int> ExecuteAsync<T>(
        DbContext context, IEnumerable<T> rows,
        bool returnGeneratedKeys,
        CancellationToken ct) where T : class
    {
        if (rows.TryGetNonEnumeratedCount(out var knownCount) && knownCount == 0)
            return 0;

        var info = EfModel.For<T>(context);
        if (context.Database.GetDbConnection() is not NpgsqlConnection connection)
            throw new NotSupportedException(
                "PgYeet supports PostgreSQL via the Npgsql EF Core provider only (Npgsql.EntityFrameworkCore.PostgreSQL).");
        var logger = context.GetService<ILoggerFactory>().CreateLogger("PgYeet");

        await context.Database.OpenConnectionAsync(ct);
        try
        {
            if (info.Identity is null || !returnGeneratedKeys)
                return checked((int)await DirectCopyAsync(connection, info, rows, logger, ct));

            var list = rows as IReadOnlyList<T> ?? rows.ToArray();
            return list.Count == 0
                ? 0
                : await StagedInsertAsync(context, connection, info, list, logger, ct);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static async Task<ulong> DirectCopyAsync<T>(
        NpgsqlConnection connection,
        EfTableInfo<T> info,
        IEnumerable<T> rows,
        ILogger logger,
        CancellationToken ct)
    {
        var columnList = string.Join(", ", info.InsertColumns.Select(c => EfModel.QuoteIdent(c.Name)));
        var copy = $"COPY {info.QuotedTable} ({columnList}) FROM STDIN (FORMAT BINARY)";
        var writers = info.InsertColumns
            .Select(c => c.Write)
            .ToArray();

        logger.LogDebug("PgYeet COPY: {Sql}", copy);
        var copied = await BinaryCopy.WriteAsync(connection, copy, writers, rows, ct);
        logger.LogDebug("PgYeet COPY done: {RowCount} rows", copied);
        return copied;
    }

    private static async Task<int> StagedInsertAsync<T>(
        DbContext context,
        NpgsqlConnection connection,
        EfTableInfo<T> info,
        IReadOnlyList<T> rows,
        ILogger logger,
        CancellationToken ct) where T : class
    {
        var cols = info.InsertColumns;
        var identity = info.Identity!;
        var temp = $"_pgyeet_{Guid.NewGuid():N}";
        var columnList = string.Join(", ", cols.Select(c => EfModel.QuoteIdent(c.Name)));

        var ambient = context.Database.CurrentTransaction;
        var transaction = ambient is null
            ? await connection.BeginTransactionAsync(ct)
            : ambient.GetDbTransaction() as NpgsqlTransaction
              ?? throw new NotSupportedException(
                  "PgYeet: the current DbContext transaction is not an NpgsqlTransaction " +
                  "(is the connection wrapped by a profiler or interceptor?).");
        try
        {
            var ddl = string.Join(", ", cols.Select(c => $"{EfModel.QuoteIdent(c.Name)} {c.StoreType}"));
            await using (var cmd = new NpgsqlCommand($"CREATE TEMP TABLE {EfModel.QuoteIdent(temp)} ({ddl}, \"__ord\" bigint) ON COMMIT DROP;", connection, transaction))
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var copy = $"COPY {EfModel.QuoteIdent(temp)} ({columnList}, \"__ord\") FROM STDIN (FORMAT BINARY)";
            logger.LogDebug("PgYeet COPY {RowCount} rows: {Sql}", rows.Count, copy);
            await using (var importer = await connection.BeginBinaryImportAsync(copy, ct))
            {
                long ord = 0;
                foreach (var row in rows)
                {
                    await importer.StartRowAsync(ct);
                    foreach (var column in cols)
                        await column.Write(importer, row, ct);
                    await importer.WriteAsync(ord++, NpgsqlDbType.Bigint, ct);
                }
                await importer.CompleteAsync(ct);
            }

            var insert =
                $"INSERT INTO {info.QuotedTable} ({columnList}) " +
                $"SELECT {columnList} FROM {EfModel.QuoteIdent(temp)} ORDER BY \"__ord\" " +
                $"RETURNING {EfModel.QuoteIdent(identity.Column)};";

            // Identity values are assigned in insertion order (__ord), so sorting the returned keys
            // ascending re-aligns them with the entities even if RETURNING comes back unordered.
            // Assumes the identity sequence has a positive INCREMENT (the default).
            var ids = new long[rows.Count];
            await using (var cmd = new NpgsqlCommand(insert, connection, transaction))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                var read = 0;
                while (await reader.ReadAsync(ct))
                {
                    if (read == ids.Length)
                        throw new InvalidOperationException(
                            $"PgYeet: INSERT ... RETURNING produced more than the expected {ids.Length} keys " +
                            "(a rule or trigger may be multiplying rows); generated keys cannot be correlated.");
                    ids[read++] = Convert.ToInt64(reader.GetValue(0));
                }
                if (read != ids.Length)
                    throw new InvalidOperationException(
                        $"PgYeet: INSERT ... RETURNING produced {read} keys for {ids.Length} rows " +
                        "(a BEFORE INSERT trigger may be filtering rows); generated keys cannot be correlated.");
            }

            Array.Sort(ids);
            for (var i = 0; i < rows.Count; i++)
                identity.AssignReserved(rows[i], ids[i]);

            if (ambient is null) await transaction.CommitAsync(ct);
            return rows.Count;
        }
        catch
        {
            if (ambient is null)
                await transaction.RollbackAsync(CancellationToken.None); // ct may already be cancelled
            throw;
        }
        finally
        {
            if (ambient is null)
                await transaction.DisposeAsync();
        }
    }
}
