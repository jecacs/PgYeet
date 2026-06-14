using Microsoft.EntityFrameworkCore;
using Xunit;

namespace PgYeet.Tests;

public sealed class YeetTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public YeetTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();   // empty tables before each test
    public Task DisposeAsync() => Task.CompletedTask;

    private TestDbContext NewContext() => new(_pg.Options);

    [Fact]
    public async Task Insert_returns_count_and_writes_back_generated_keys()
    {
        await using var db = NewContext();
        var people = MakePeople(50);

        var inserted = await db.People.YeetAsync(people);

        Assert.Equal(50, inserted);
        Assert.All(people, p => Assert.True(p.Id > 0));
        Assert.Equal(50, people.Select(p => p.Id).Distinct().Count());
        Assert.Equal(50, await db.People.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Insert_without_returnIds_persists_but_leaves_keys_default()
    {
        await using var db = NewContext();
        var people = MakePeople(20);

        var inserted = await db.People.YeetAsync(people, returnGeneratedKeys: false);

        Assert.Equal(20, inserted);
        Assert.All(people, p => Assert.Equal(0, p.Id));   // no write-back
        Assert.Equal(20, await db.People.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Roundtrips_all_column_types_including_null_and_converter()
    {
        await using var db = NewContext();
        var createdAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var person = new Person
        {
            Name = "Ada", Email = "ada@example.com",
            IsActive = true, CreatedAt = createdAt,
            Score = null, Status = Status.Inactive,
        };

        await db.People.YeetAsync(new[] { person });

        var loaded = await db.People.AsNoTracking().SingleAsync();
        Assert.Equal(person.Id, loaded.Id);          // key written back matches the stored row
        Assert.Equal("Ada", loaded.Name);
        Assert.Equal("ada@example.com", loaded.Email);
        Assert.True(loaded.IsActive);
        Assert.Equal(createdAt, loaded.CreatedAt);
        Assert.Null(loaded.Score);
        Assert.Equal(Status.Inactive, loaded.Status);
    }

    [Fact]
    public async Task App_assigned_pk_uses_direct_copy()
    {
        await using var db = NewContext();
        var items = new[]
        {
            new Item { Id = 100, Code = "A", Quantity = 1 },
            new Item { Id = 200, Code = "B", Quantity = 2 },
        };

        var inserted = await db.Items.YeetAsync(items);

        Assert.Equal(2, inserted);
        var ids = await db.Items.AsNoTracking().Select(i => i.Id).OrderBy(i => i).ToListAsync();
        Assert.Equal(new[] { 100, 200 }, ids);
    }

    [Fact]
    public async Task Empty_input_returns_zero()
    {
        await using var db = NewContext();
        Assert.Equal(0, await db.People.YeetAsync(Array.Empty<Person>()));
    }

    [Fact]
    public async Task Honors_ambient_transaction_rollback()
    {
        await using var db = NewContext();

        await using (await db.Database.BeginTransactionAsync())
        {
            await db.People.YeetAsync(MakePeople(10));
            // no commit -> rolled back on dispose
        }

        Assert.Equal(0, await db.People.AsNoTracking().CountAsync());
    }

    private static Person[] MakePeople(int n) =>
        Enumerable.Range(0, n).Select(i => new Person
        {
            Name = "p" + i,
            Email = $"p{i}@example.com",
            IsActive = i % 2 == 0,
            CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Score = i % 3 == 0 ? (int?)null : i,
            Status = Status.Active,
        }).ToArray();
}
