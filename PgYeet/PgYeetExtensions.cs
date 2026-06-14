using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace PgYeet;

/// <summary>
/// EF Core bulk-insert extensions for PostgreSQL, backed by binary COPY
/// </summary>
public static class PgYeetExtensions
{
    /// <summary>
    /// Bulk-inserts <paramref name="entities"/> into the table mapped by <typeparamref name="T"/> using
    /// PostgreSQL binary COPY. If the entity has a single store-generated (identity) primary key, the
    /// generated keys are written back onto the entities. Returns the number of rows inserted.
    /// Pass <paramref name="returnGeneratedKeys"/>=false to skip the key write-back and do a single
    /// direct COPY (faster; the entities' keys stay default).
    /// </summary>
    public static Task<int> YeetAsync<T>(
        this DbSet<T> dbSet,
        IEnumerable<T> entities,
        CancellationToken ct = default,
        bool returnGeneratedKeys = true)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(dbSet);
        ArgumentNullException.ThrowIfNull(entities);

        var context = dbSet.GetService<ICurrentDbContext>().Context;
        var rows = entities as IReadOnlyList<T> ?? entities.ToArray();
        return BulkInsert.ExecuteAsync(context, rows, returnGeneratedKeys, ct);
    }
}
