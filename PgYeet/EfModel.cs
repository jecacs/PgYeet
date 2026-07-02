using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace PgYeet;

internal sealed record EfColumn<T>(
    string Name,
    string StoreType,
    Func<NpgsqlBinaryImporter, T, CancellationToken, ValueTask> Write);

internal sealed record EfIdentity<T>(
    string Column,
    Action<T, long> AssignReserved);

internal sealed record EfTableInfo<T>(
    string QuotedTable,
    IReadOnlyList<EfColumn<T>> InsertColumns,
    EfIdentity<T>? Identity);

/// <summary>Builds and caches the COPY mapping for an entity type from its EF Core model.</summary>
internal static class EfModel
{
    // Keyed by the runtime model instance rather than the context type: contexts with dynamic
    // models (IModelCacheKeyFactory, e.g. schema-per-tenant) get one entry per model, and each
    // entry dies with its model instead of pinning a stale mapping.
    private static readonly ConditionalWeakTable<IModel, ConcurrentDictionary<Type, object>> Cache = new();

    public static EfTableInfo<T> For<T>(DbContext context) where T : class
        => (EfTableInfo<T>)Cache.GetOrCreateValue(context.Model)
            .GetOrAdd(typeof(T), static (_, ctx) => Build<T>(ctx), context);

    private static EfTableInfo<T> Build<T>(DbContext context) where T : class
    {
        var entityType = context.Model.FindEntityType(typeof(T))
            ?? throw new InvalidOperationException(
                $"'{typeof(T).Name}' is not part of the EF model of {context.GetType().Name}.");

        var store = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table)
            ?? throw new InvalidOperationException($"'{typeof(T).Name}' is not mapped to a table.");

        GuardUnsupportedMapping<T>(entityType);

        var identityProperty = FindIdentity(entityType);

        var columns = new List<EfColumn<T>>();
        foreach (var property in entityType.GetProperties())
        {
            if (ReferenceEquals(property, identityProperty)) continue;   // DB-generated, never written
            if (property.GetComputedColumnSql() is not null) continue;   // server-computed

            if (property.PropertyInfo is not { } propertyInfo)
            {
                // Shadow property: no CLR member to read the value from. Omitting the column is
                // only safe when the database can fill it on its own — otherwise fail fast here
                // instead of a cryptic not-null violation (typical culprits: required shadow FKs).
                if (!property.IsColumnNullable(store)
                    && property.GetDefaultValueSql() is null
                    && !property.TryGetDefaultValue(out _)
                    && property.ValueGenerated == ValueGenerated.Never)
                    throw new NotSupportedException(
                        $"PgYeet: '{typeof(T).Name}.{property.Name}' is a required shadow property with no " +
                        "database default — PgYeet cannot supply a value for it. " +
                        "Map it to a CLR property, make it nullable, or give it a default.");
                continue;
            }

            var column = property.GetColumnName(store);
            if (column is null) continue;

            var storeType = property.GetColumnType();
            columns.Add(new EfColumn<T>(column, storeType, BuildWriter<T>(property, propertyInfo, StripFacets(storeType))));
        }

        EfIdentity<T>? identity = null;
        if (identityProperty?.PropertyInfo is { } idInfo)
        {
            identity = new EfIdentity<T>(
                identityProperty.GetColumnName(store)!,
                BuildIdAssigner<T>(identityProperty, idInfo));
        }

        return new EfTableInfo<T>(
            QuoteQualified(entityType.GetSchema(), entityType.GetTableName()!),
            columns,
            identity);
    }

    /// <summary>
    /// Rejects mappings whose columns PgYeet would silently drop (inserting incomplete rows) —
    /// better a clear exception up front than NULLs in the database.
    /// </summary>
    private static void GuardUnsupportedMapping<T>(IEntityType entityType)
    {
        if (entityType.FindDiscriminatorProperty() is not null)
            throw new NotSupportedException(
                $"PgYeet: '{typeof(T).Name}' uses TPH inheritance (discriminator column) — not supported yet.");

        if (entityType.GetTableMappings().Skip(1).Any())
            throw new NotSupportedException(
                $"PgYeet: '{typeof(T).Name}' is mapped to more than one table (TPT inheritance or entity " +
                "splitting) — not supported.");

        var ownedNavigations = entityType.GetNavigations()
            .Where(n => n.TargetEntityType.IsOwned())
            .Select(n => n.Name)
            .ToList();
        if (ownedNavigations.Count > 0)
            throw new NotSupportedException(
                $"PgYeet: '{typeof(T).Name}' has owned-type navigation(s) ({string.Join(", ", ownedNavigations)}) " +
                "— their columns would be skipped; not supported yet.");

        if (entityType.GetComplexProperties().Any())
            throw new NotSupportedException(
                $"PgYeet: '{typeof(T).Name}' has complex-type properties " +
                $"({string.Join(", ", entityType.GetComplexProperties().Select(p => p.Name))}) — not supported yet.");

        var table = entityType.GetTableMappings().FirstOrDefault()?.Table;
        if (table is not null && table.EntityTypeMappings.Skip(1).Any())
            throw new NotSupportedException(
                $"PgYeet: table '{table.Name}' is shared by multiple entity types (table splitting) — not supported.");
    }

    /// <summary>
    /// The single-column PK is treated as a DB-generated identity only when PostgreSQL actually
    /// generates it (identity/serial). Other store-generated strategies (sequence or uuid defaults,
    /// HiLo) are rejected: PgYeet bypasses EF value generation, so it can neither supply the value
    /// nor correlate written-back keys. Client-generated keys (e.g. plain Guid) fall through and
    /// are copied like a normal column — the caller must set them.
    /// </summary>
    private static IProperty? FindIdentity(IEntityType entityType)
    {
        var pk = entityType.FindPrimaryKey();
        if (pk is not { Properties.Count: 1 }) return null;

        var property = pk.Properties[0];
        if (property.ValueGenerated != ValueGenerated.OnAdd) return null;

        var strategy = property.GetValueGenerationStrategy();
        if (strategy is NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
            or NpgsqlValueGenerationStrategy.IdentityAlwaysColumn
            or NpgsqlValueGenerationStrategy.SerialColumn)
            return property;

        if (strategy != NpgsqlValueGenerationStrategy.None
            || property.GetDefaultValueSql() is not null
            || property.TryGetDefaultValue(out _))
            throw new NotSupportedException(
                $"PgYeet: primary key '{property.Name}' is store-generated but is not a PostgreSQL " +
                $"identity/serial column (strategy: {strategy}). Assign key values in the application and " +
                "map the key with ValueGeneratedNever(), or use an identity column.");

        return null; // client-generated (e.g. Guid): treated as an app-assigned column
    }

    private static Func<NpgsqlBinaryImporter, T, CancellationToken, ValueTask> BuildWriter<T>(
        IReadOnlyProperty property,
        PropertyInfo propertyInfo, string dataTypeName)
    {
        // HasConversion can surface either as an explicit converter or only via the type mapping:
        // in the runtime model GetValueConverter() is often null while the mapping still carries it.
        var converter = property.GetValueConverter() ?? property.FindTypeMapping()?.Converter;
        if (converter is null)
            return BuildTypedWriter<T>(propertyInfo, dataTypeName);

        var providerType = Nullable.GetUnderlyingType(converter.ProviderClrType) ?? converter.ProviderClrType;
        return MakeBoxedWriter<T>(providerType, dataTypeName, e => converter.ConvertToProvider(propertyInfo.GetValue(e)));
    }

    private static Func<NpgsqlBinaryImporter, T, CancellationToken, ValueTask> BuildTypedWriter<T>(
        PropertyInfo property,
        string dataTypeName)
    {
        var getMethod = property.GetMethod
            ?? throw new InvalidOperationException($"Property '{property.Name}' has no getter.");

        var propType = property.PropertyType;
        var underlying = Nullable.GetUnderlyingType(propType);
        var getter = getMethod.CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(T), propType));

        var (helperName, typeArgs) =
            underlying is not null ? (nameof(NullableWriter), [typeof(T), underlying]) :
            propType.IsValueType   ? (nameof(ValueWriter), [typeof(T), propType]) :
                                     (nameof(RefWriter),      new[] { typeof(T), propType });

        var helper = typeof(EfModel).GetMethod(helperName, BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeArgs);

        return (Func<NpgsqlBinaryImporter, T, CancellationToken, ValueTask>)
            helper.Invoke(null, [getter, dataTypeName])!;
    }

    /// <summary>
    /// Writes the reserved (RETURNING) key back onto the entity. Common integer identities go
    /// through a compiled typed setter — no per-row reflection or boxing; converter-backed keys
    /// (e.g. strongly-typed IDs) are mapped back through the converter.
    /// </summary>
    private static Action<T, long> BuildIdAssigner<T>(IReadOnlyProperty property, PropertyInfo propertyInfo)
    {
        var converter = property.GetValueConverter() ?? property.FindTypeMapping()?.Converter;
        if (converter is not null)
        {
            var providerType = Nullable.GetUnderlyingType(converter.ProviderClrType) ?? converter.ProviderClrType;
            return (entity, reserved) =>
                propertyInfo.SetValue(entity, converter.ConvertFromProvider(ConvertId(reserved, providerType)));
        }

        var setMethod = propertyInfo.SetMethod;
        var clrType = propertyInfo.PropertyType;
        if (setMethod is not null)
        {
            if (clrType == typeof(int))
            {
                var set = (Action<T, int>)setMethod.CreateDelegate(typeof(Action<T, int>));
                return (entity, reserved) => set(entity, checked((int)reserved));
            }
            if (clrType == typeof(long))
                return (Action<T, long>)setMethod.CreateDelegate(typeof(Action<T, long>));
            if (clrType == typeof(short))
            {
                var set = (Action<T, short>)setMethod.CreateDelegate(typeof(Action<T, short>));
                return (entity, reserved) => set(entity, checked((short)reserved));
            }
        }

        return (entity, reserved) => propertyInfo.SetValue(entity, ConvertId(reserved, clrType));
    }

    private static Func<NpgsqlBinaryImporter, T, CancellationToken, ValueTask> ValueWriter<T, TV>(
        Func<T, TV> get,
        string dataTypeName) where TV : struct
        => async (importer, entity, ct) => await importer.WriteAsync(get(entity), dataTypeName, ct);

    private static Func<NpgsqlBinaryImporter, T, CancellationToken, ValueTask> NullableWriter<T, TV>(
        Func<T, TV?> get,
        string dataTypeName) where TV : struct
        => async (importer, entity, ct) =>
        {
            var value = get(entity);
            if (value.HasValue) await importer.WriteAsync(value.Value, dataTypeName, ct);
            else await importer.WriteNullAsync(ct);
        };

    private static Func<NpgsqlBinaryImporter, T, CancellationToken, ValueTask> RefWriter<T, TR>(
        Func<T, TR?> get, string dataTypeName) where TR : class
        => async (importer, entity, ct) =>
        {
            var value = get(entity);
            if (value is not null) await importer.WriteAsync(value, dataTypeName, ct);
            else await importer.WriteNullAsync(ct);
        };

    private static Func<NpgsqlBinaryImporter, T, CancellationToken, ValueTask> MakeBoxedWriter<T>(
        Type providerType, string dataTypeName, Func<T, object?> get)
    {
        var typed = typeof(EfModel).GetMethod(nameof(BoxedWriter), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(T), providerType);

        return (Func<NpgsqlBinaryImporter, T, CancellationToken, ValueTask>)
            typed.Invoke(null, [dataTypeName, get])!;
    }

    private static Func<NpgsqlBinaryImporter, T, CancellationToken, ValueTask> BoxedWriter<T, TProvider>(
        string dataTypeName,
        Func<T, object?> get)
        => async (importer, entity, ct) =>
        {
            var value = get(entity);
            if (value is null or DBNull) await importer.WriteNullAsync(ct);
            else await importer.WriteAsync((TProvider)value, dataTypeName, ct);
        };

    private static object ConvertId(long value, Type targetClrType)
    {
        var t = Nullable.GetUnderlyingType(targetClrType) ?? targetClrType;
        if (t == typeof(long)) return value;
        if (t == typeof(int)) return checked((int)value);
        if (t == typeof(short)) return checked((short)value);
        return Convert.ChangeType(value, t);
    }

    private static string StripFacets(string storeType)
    {
        var open = storeType.IndexOf('(');
        if (open < 0) return storeType;
        var close = storeType.IndexOf(')', open);
        var suffix = close >= 0 ? storeType[(close + 1)..] : string.Empty;
        return storeType[..open] + suffix;
    }

    internal static string QuoteIdent(string ident) => $"\"{ident.Replace("\"", "\"\"")}\"";

    internal static string QuoteQualified(string? schema, string table)
        => string.IsNullOrEmpty(schema) ? QuoteIdent(table) : $"{QuoteIdent(schema)}.{QuoteIdent(table)}";
}
