using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace PgYeet.Tests;

/// <summary>Spins up a throwaway PostgreSQL container once per test class and creates the schema.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string DefaultPostgresImage =
        "postgres:18.6-alpine3.24@sha256:d3e1620b530c944afa6e887d22eb899824da68e19c52024bf98f5220c88a65b2";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(
        Environment.GetEnvironmentVariable("PGYEET_TEST_POSTGRES_IMAGE") ?? DefaultPostgresImage)
        .Build();

    public DbContextOptions<TestDbContext> Options { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;

        await using var db = new TestDbContext(Options);
        await db.Database.EnsureCreatedAsync();
    }

    public async Task ResetAsync()
    {
        await using var db = new TestDbContext(Options);
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE people, orders, big_orders RESTART IDENTITY; " +
            "TRUNCATE TABLE items, gadgets, composite_items, composite_guid_items;");
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}
