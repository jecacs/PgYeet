using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace PgYeet;

/// <summary>
/// Provides PostgreSQL bulk-insert extensions for Entity Framework Core.
/// </summary>
public static class PgYeetExtensions
{
    /// <summary>
    /// Inserts <paramref name="entities"/> into the PostgreSQL table mapped by <typeparamref name="T"/>
    /// using binary COPY and returns the number of rows inserted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a supported single-column identity or serial primary key, the default path buffers the
    /// input and writes generated values back to the supplied objects. Set
    /// <paramref name="returnGeneratedKeys"/> to <see langword="false"/> to stream the input directly
    /// without changing the objects' key values.
    /// </para>
    /// <para>
    /// This method executes immediately and bypasses EF Core change tracking and
    /// <see cref="DbContext.SaveChanges()"/>. It does not attach the supplied objects or change their
    /// <see cref="EntityState"/>. Do not add the same objects to the context before calling it.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The mapped entity CLR type.</typeparam>
    /// <param name="dbSet">The set whose EF Core mapping is used for the insert.</param>
    /// <param name="entities">The entities to insert.</param>
    /// <param name="returnGeneratedKeys">
    /// Whether supported identity or serial values should be returned and assigned to the objects.
    /// </param>
    /// <param name="ct">A token that can cancel the asynchronous database operation.</param>
    /// <returns>The number of rows inserted.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="dbSet"/> or <paramref name="entities"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="entities"/> contains a null element, or the generated-key path receives the
    /// same object reference more than once.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The direct streaming path would exceed <see cref="int.MaxValue"/> rows. The in-progress COPY
    /// is aborted before the exception is thrown.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="T"/> is not part of the EF Core model, is not mapped to a table, or returned
    /// generated keys cannot be correlated with the supplied objects.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The context does not use Npgsql, or the EF Core mapping/key strategy is unsupported.
    /// </exception>
    public static Task<int> YeetAsync<T>(
        this DbSet<T> dbSet,
        IEnumerable<T> entities,
        bool returnGeneratedKeys = true,
        CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(dbSet);
        ArgumentNullException.ThrowIfNull(entities);

        var context = dbSet.GetService<ICurrentDbContext>().Context;
        return BulkInsert.ExecuteAsync(context, entities, returnGeneratedKeys, ct);
    }
}
