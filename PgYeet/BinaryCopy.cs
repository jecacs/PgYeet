using Npgsql;

namespace PgYeet;

internal static class BinaryCopy
{
    public static async Task<int> WriteAsync<T>(
        NpgsqlConnection connection,
        string copyCommand,
        IReadOnlyList<Func<NpgsqlBinaryImporter, T, CancellationToken, ValueTask>> writers,
        IEnumerable<T> entities,
        CancellationToken ct = default)
    {
        var importer = await connection.BeginBinaryImportAsync(copyCommand, ct);
        Exception? operationFailure = null;
        try
        {
            var rowCount = 0;
            foreach (var row in entities)
            {
                if (row is null)
                    throw new ArgumentException("The entities sequence contains a null element.", nameof(entities));
                if (rowCount == int.MaxValue)
                    throw new ArgumentOutOfRangeException(
                        nameof(entities),
                        "A single PgYeet operation cannot insert more than Int32.MaxValue rows.");

                await importer.StartRowAsync(ct);
                for (var i = 0; i < writers.Count; i++)
                    await writers[i](importer, row, ct);
                rowCount++;
            }

            // PostgreSQL reports the rows actually inserted, which can be lower than the input count
            // when a BEFORE INSERT trigger returns NULL. The input limit above makes the conversion safe.
            return checked((int)await importer.CompleteAsync(ct));
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
                await importer.DisposeAsync();
            }
            catch (Exception disposeException) when (operationFailure is not null)
            {
                operationFailure.Data["PgYeet binary importer disposal failure"] = disposeException;
            }
        }
    }
}
