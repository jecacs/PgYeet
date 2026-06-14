using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace PgYeet.Tests;

/// <summary>Spins up a throwaway PostgreSQL container once per test class and creates the schema.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
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
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE people RESTART IDENTITY; TRUNCATE TABLE items;");
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}
