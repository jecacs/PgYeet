using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace PgYeet;

internal sealed record EfColumn<T>(
    string Name,
    string StoreType,
    Func<NpgsqlBinaryImporter, T, CancellationToken, ValueTask> Write);

internal sealed record EfIdentity<T>(
    string Column,
    Func<NpgsqlBinaryImporter, T, CancellationToken, ValueTask> Write,
    Action<T, long> AssignReserved);

internal sealed record EfTableInfo<T>(
    string QuotedTable,
    IReadOnlyList<EfColumn<T>> InsertColumns,
    EfIdentity<T>? Identity);

/// <summary>Builds and caches the COPY mapping for an entity type from its EF Core model.</summary>
internal static class EfModel
{
    private static readonly ConcurrentDictionary<(Type Entity, Type Context), object> Cache = new();

    public static EfTableInfo<T> For<T>(DbContext context) where T : class
        => (EfTableInfo<T>)Cache.GetOrAdd((typeof(T), context.GetType()), _ => Build<T>(context));

    private static EfTableInfo<T> Build<T>(DbContext context) where T : class
    {
        var entityType = context.Model.FindEntityType(typeof(T))
            ?? throw new InvalidOperationException(
                $"'{typeof(T).Name}' is not part of the EF model of {context.GetType().Name}.");

        var store = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table)
            ?? throw new InvalidOperationException($"'{typeof(T).Name}' is not mapped to a table.");

        var pk = entityType.FindPrimaryKey();
        var identityProperty = pk is { Properties.Count: 1 } && pk.Properties[0].ValueGenerated == ValueGenerated.OnAdd
            ? pk.Properties[0]
            : null;

        var columns = new List<EfColumn<T>>();
        foreach (var property in entityType.GetProperties())
        {
            if (property.GetComputedColumnSql() is not null) continue;   // server-computed
            if (ReferenceEquals(property, identityProperty)) continue;   // handled separately
            if (property.PropertyInfo is not { } propertyInfo) continue; // shadow property

            var column = property.GetColumnName(store);
            if (column is null) continue;

            var storeType = property.GetColumnType();
            columns.Add(new EfColumn<T>(column, storeType, BuildWriter<T>(property, propertyInfo, StripFacets(storeType))));
        }

        EfIdentity<T>? identity = null;
        if (identityProperty?.PropertyInfo is { } idInfo)
        {
            var idClrType = idInfo.PropertyType;
            var idWrite = BuildWriter<T>(identityProperty, idInfo, StripFacets(identityProperty.GetColumnType()));
            identity = new EfIdentity<T>(
                identityProperty.GetColumnName(store)!,
                idWrite,
                (entity, reserved) => idInfo.SetValue(entity, ConvertId(reserved, idClrType)));
        }

        return new EfTableInfo<T>(
            QuoteQualified(entityType.GetSchema(), entityType.GetTableName()!),
            columns,
            identity);
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
