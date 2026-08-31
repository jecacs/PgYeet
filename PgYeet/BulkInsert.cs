using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace PgYeet;

internal static class BulkInsert
{
    private static readonly Action<ILogger, string, Exception?> LogCopyStarting = LoggerMessage.Define<string>(
        LogLevel.Debug,
        new EventId(1, nameof(LogCopyStarting)),
        "PgYeet COPY: {Sql}");

    private static readonly Action<ILogger, int, Exception?> LogCopyCompleted = LoggerMessage.Define<int>(
        LogLevel.Debug,
        new EventId(2, nameof(LogCopyCompleted)),
        "PgYeet COPY done: {RowCount} rows");

    private static readonly Action<ILogger, int, string, Exception?> LogStagedCopyStarting =
        LoggerMessage.Define<int, string>(
            LogLevel.Debug,
            new EventId(3, nameof(LogStagedCopyStarting)),
            "PgYeet COPY {RowCount} rows: {Sql}");

    public static async Task<int> ExecuteAsync<T>(
        DbContext context, IEnumerable<T> rows,
        bool returnGeneratedKeys,
        CancellationToken ct) where T : class
    {
        if (rows.TryGetNonEnumeratedCount(out var knownCount) && knownCount == 0)
            return 0;

        var info = EfModel.For<T>(context);
        IReadOnlyList<T>? stagedRows = null;
        if (info.Identity is not null && returnGeneratedKeys)
        {
            stagedRows = rows as IReadOnlyList<T> ?? rows.ToArray();
            if (stagedRows.Count == 0)
                return 0;
            GuardGeneratedKeyInput(stagedRows);
        }

        if (context.Database.GetDbConnection() is not NpgsqlConnection connection)
            throw new NotSupportedException(
                "PgYeet supports PostgreSQL via the Npgsql EF Core provider only (Npgsql.EntityFrameworkCore.PostgreSQL).");
        var logger = context.GetService<ILoggerFactory>().CreateLogger("PgYeet");

        await context.Database.OpenConnectionAsync(ct);
        Exception? operationFailure = null;
        try
        {
            if (stagedRows is null)
                return await DirectCopyAsync(connection, info, rows, logger, ct);

            return await StagedInsertAsync(context, connection, info, stagedRows, logger, ct);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                await context.Database.CloseConnectionAsync();
            }
            catch (Exception closeException) when (operationFailure is not null)
            {
                operationFailure.Data["PgYeet connection close failure"] = closeException;
            }
        }
    }

    private static async Task<int> DirectCopyAsync<T>(
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

        LogCopyStarting(logger, copy, null);
        var copied = await BinaryCopy.WriteAsync(connection, copy, writers, rows, ct);
        LogCopyCompleted(logger, copied, null);
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
        object?[]? originalKeys = null;
        Exception? operationFailure = null;
        try
        {
            await GuardIdentitySequenceAsync(connection, transaction, info, ct);

            var ddl = string.Join(", ", cols.Select(c => $"{EfModel.QuoteIdent(c.Name)} {c.StoreType}"));
            await using (var cmd = new NpgsqlCommand($"CREATE TEMP TABLE {EfModel.QuoteIdent(temp)} ({ddl}, \"__ord\" bigint) ON COMMIT DROP;", connection, transaction))
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var copy = $"COPY {EfModel.QuoteIdent(temp)} ({columnList}, \"__ord\") FROM STDIN (FORMAT BINARY)";
            LogStagedCopyStarting(logger, rows.Count, copy, null);
            var importer = await connection.BeginBinaryImportAsync(copy, ct);
            Exception? copyFailure = null;
            try
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
            catch (Exception exception)
            {
                copyFailure = exception;
                throw;
            }
            finally
            {
                try
                {
                    await importer.DisposeAsync();
                }
                catch (Exception disposeException) when (copyFailure is not null)
                {
                    copyFailure.Data["PgYeet binary importer disposal failure"] = disposeException;
                }
            }

            var insert =
                $"INSERT INTO {info.QuotedTable} ({columnList}) " +
                $"SELECT {columnList} FROM {EfModel.QuoteIdent(temp)} ORDER BY \"__ord\" " +
                $"RETURNING {EfModel.QuoteIdent(identity.Column)};";

            // Identity values are assigned in insertion order (__ord), so sorting the returned keys
            // ascending re-aligns them with the entities even if RETURNING comes back unordered.
            // The catalog preflight above proves that the actual backing sequence is positive and
            // non-cyclic. Identity-rewriting BEFORE INSERT triggers remain unsupported.
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
                    ids[read++] = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                }
                if (read != ids.Length)
                    throw new InvalidOperationException(
                        $"PgYeet: INSERT ... RETURNING produced {read} keys for {ids.Length} rows " +
                        "(a BEFORE INSERT trigger may be filtering rows); generated keys cannot be correlated.");
            }

            Array.Sort(ids);
            for (var i = 0; i < rows.Count; i++)
                identity.ValidateReserved(ids[i]);

            var keySnapshot = new object?[rows.Count];
            for (var i = 0; i < rows.Count; i++)
                keySnapshot[i] = identity.ReadCurrent(rows[i]);
            originalKeys = keySnapshot;

            for (var i = 0; i < rows.Count; i++)
                identity.AssignReserved(rows[i], ids[i]);

            if (ambient is null) await transaction.CommitAsync(ct);
            return rows.Count;
        }
        catch (Exception operationException)
        {
            operationFailure = operationException;
            List<Exception>? cleanupFailures = null;
            if (ambient is null)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None); // ct may already be cancelled
                }
                catch (Exception ex)
                {
                    (cleanupFailures ??= []).Add(ex);
                }
            }

            if (originalKeys is not null)
            {
                for (var i = rows.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        identity.Restore(rows[i], originalKeys[i]);
                    }
                    catch (Exception ex)
                    {
                        (cleanupFailures ??= []).Add(ex);
                    }
                }
            }

            if (cleanupFailures is not null)
                operationException.Data["PgYeet cleanup failures"] = new AggregateException(cleanupFailures);

            throw;
        }
        finally
        {
            if (ambient is null)
            {
                try
                {
                    await transaction.DisposeAsync();
                }
                catch (Exception disposeException) when (operationFailure is not null)
                {
                    operationFailure.Data["PgYeet transaction disposal failure"] = disposeException;
                }
            }
        }
    }

    private static async Task GuardIdentitySequenceAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EfTableInfo<T> info,
        CancellationToken ct)
    {
        const string sql =
            """
            SELECT sequence.seqincrement, sequence.seqcycle
            FROM pg_catalog.pg_sequence AS sequence
            WHERE sequence.seqrelid = pg_catalog.pg_get_serial_sequence(@table_name, @column_name)::regclass;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("table_name", NpgsqlDbType.Text, info.QuotedTable);
        command.Parameters.AddWithValue("column_name", NpgsqlDbType.Text, info.Identity!.Column);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new NotSupportedException(
                $"PgYeet: PostgreSQL did not expose a backing sequence for identity/serial column " +
                $"'{info.Identity.Column}' on {info.QuotedTable}. The database schema may not match the EF model.");

        var increment = reader.GetInt64(0);
        var isCyclic = reader.GetBoolean(1);
        if (increment <= 0)
            throw new NotSupportedException(
                $"PgYeet: identity/serial column '{info.Identity.Column}' on {info.QuotedTable} uses a " +
                "non-positive sequence increment. Generated-key write-back requires a positive increment.");

        if (isCyclic)
            throw new NotSupportedException(
                $"PgYeet: identity/serial column '{info.Identity.Column}' on {info.QuotedTable} uses a " +
                "cyclic sequence. Generated-key write-back does not support sequence wraparound.");
    }

    private static void GuardGeneratedKeyInput<T>(IReadOnlyList<T> entities) where T : class
    {
        var unique = new HashSet<T>(ReferenceEqualityComparer.Instance);
        foreach (var row in entities)
        {
            if (row is null)
                throw new ArgumentException("The entities sequence contains a null element.", nameof(entities));

            if (!unique.Add(row))
                throw new ArgumentException(
                    "The entities sequence contains the same object reference more than once. " +
                    "Generated keys cannot be written back unambiguously; use distinct objects or set " +
                    "returnGeneratedKeys to false.",
                    nameof(entities));
        }
    }
}
