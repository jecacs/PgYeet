using System.Reflection;
using Xunit;

namespace PgYeet.Tests;

public sealed class PublicApiTests
{
    [Fact]
    public void Stable_surface_contains_only_the_extension_api()
    {
        var publicTypes = typeof(PgYeetExtensions).Assembly
            .GetExportedTypes()
            .Select(type => type.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "PgYeet.PgYeetExtensions" }, publicTypes);
    }

    [Fact]
    public void YeetAsync_has_the_stable_1_0_signature()
    {
        var method = typeof(PgYeetExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(candidate => candidate.Name == nameof(PgYeetExtensions.YeetAsync));

        Assert.True(method.IsGenericMethodDefinition);
        Assert.Equal(typeof(Task<int>), method.ReturnType);
        Assert.Equal(
            new[] { "dbSet", "entities", "returnGeneratedKeys", "ct" },
            method.GetParameters().Select(parameter => parameter.Name));
    }
}
