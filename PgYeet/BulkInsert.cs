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
        DbContext context, IReadOnlyList<T> rows,
        bool returnGeneratedKeys,
        CancellationToken ct) where T : class
    {
        if (rows.Count == 0)
            return 0;

        var info = EfModel.For<T>(context);
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        var logger = context.GetService<ILoggerFactory>().CreateLogger("PgYeet");

        await context.Database.OpenConnectionAsync(ct);
        try
        {
            return info.Identity is null || !returnGeneratedKeys
                ? (int) await DirectCopyAsync(connection, info, rows, logger, ct)
                : await StagedInsertAsync(context, connection, info, rows, logger, ct);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static Task<ulong> DirectCopyAsync<T>(
        NpgsqlConnection connection,
        EfTableInfo<T> info,
        IReadOnlyCollection<T> rows,
        ILogger logger,
        CancellationToken ct)
    {
        var columnList = string.Join(", ", info.InsertColumns.Select(c => EfModel.QuoteIdent(c.Name)));
        var copy = $"COPY {info.QuotedTable} ({columnList}) FROM STDIN (FORMAT BINARY)";
        var writers = info.InsertColumns
            .Select(c => c.Write)
            .ToArray();

        logger.LogDebug("PgYeet COPY {RowCount} rows: {Sql}", rows.Count, copy);
        return BinaryCopy.WriteAsync(connection, copy, writers, rows, ct);
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
        var transaction = ambient?.GetDbTransaction() as NpgsqlTransaction ?? await connection.BeginTransactionAsync(ct);
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

            // Identity is monotonic with insertion order, so sorting the returned keys ascending
            // lines them up with the entities (robust even if RETURNING comes back unordered).
            var ids = new long[rows.Count];
            await using (var cmd = new NpgsqlCommand(insert, connection, transaction))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                var i = 0;
                while (await reader.ReadAsync(ct))
                    ids[i++] = Convert.ToInt64(reader.GetValue(0));
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
                await transaction.RollbackAsync(ct);
            throw;
        }
        finally
        {
            if (ambient is null)
                await transaction.DisposeAsync();
        }
    }
}
