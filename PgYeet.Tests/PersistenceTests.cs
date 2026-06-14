using Microsoft.EntityFrameworkCore;
using Xunit;

namespace PgYeet.Tests;

/// <summary>
/// Integration tests focused on one thing: proving rows actually land in PostgreSQL.
/// Every assertion reads the data back through a *fresh* DbContext (a new physical connection)
/// or raw SQL, so nothing here can pass on in-memory change-tracker state alone.
/// </summary>
public sealed class PersistenceTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public PersistenceTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();   // empty tables before each test
    public Task DisposeAsync() => Task.CompletedTask;

    private TestDbContext NewContext() => new(_pg.Options);

    [Fact]
    public async Task Rows_are_readable_from_a_separate_connection()
    {
        await using (var writer = NewContext())
            await writer.People.YeetAsync(MakePeople(100));

        // brand-new context => its own connection => can only see committed DB state
        await using var reader = NewContext();
        Assert.Equal(100, await reader.People.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Count_is_visible_via_raw_sql()
    {
        await using var db = NewContext();
        await db.People.YeetAsync(MakePeople(37));

        var count = await db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM people")
            .SingleAsync();

        Assert.Equal(37, count);
    }

    [Fact]
    public async Task Written_back_key_points_at_the_matching_row()
    {
        // Each person gets a unique Name/Score; after insert every entity's generated Id must
        // address the row that actually holds *that* entity's data — proves the key write-back
        // correlates rows correctly, not just that the keys are distinct.
        var people = MakePeople(500);
        await using (var writer = NewContext())
            await writer.People.YeetAsync(people);

        var expectedById = people.ToDictionary(p => p.Id, p => (p.Name, p.Score));

        await using var reader = NewContext();
        var rows = await reader.People.AsNoTracking()
            .Select(p => new { p.Id, p.Name, p.Score })
            .ToListAsync();

        Assert.Equal(people.Length, rows.Count);
        foreach (var row in rows)
        {
            Assert.True(expectedById.TryGetValue(row.Id, out var data), $"unexpected Id {row.Id}");
            Assert.Equal(data.Name, row.Name);
            Assert.Equal(data.Score, row.Score);
        }
    }

    [Fact]
    public async Task Large_batch_persists_every_row_with_unique_keys()
    {
        const int n = 10_000;
        await using (var writer = NewContext())
        {
            var people = MakePeople(n);
            var inserted = await writer.People.YeetAsync(people);

            Assert.Equal(n, inserted);
            Assert.Equal(n, people.Select(p => p.Id).Distinct().Count());
        }

        await using var reader = NewContext();
        Assert.Equal(n, await reader.People.AsNoTracking().CountAsync());
        Assert.Equal(n, await reader.People.AsNoTracking().Select(p => p.Id).Distinct().CountAsync());
    }

    [Fact]
    public async Task Sequential_inserts_accumulate_without_key_collisions()
    {
        await using var db = NewContext();
        var first = MakePeople(40);
        var second = MakePeople(60);

        await db.People.YeetAsync(first);
        await db.People.YeetAsync(second);

        await using var reader = NewContext();
        Assert.Equal(100, await reader.People.AsNoTracking().CountAsync());

        var allIds = first.Concat(second).Select(p => p.Id).ToList();
        Assert.Equal(100, allIds.Distinct().Count());                 // no overlap
        Assert.True(second.Min(p => p.Id) > first.Max(p => p.Id));    // second batch came after
    }

    [Fact]
    public async Task Special_characters_round_trip_byte_for_byte()
    {
        // Binary COPY transmits values out-of-band, so quotes/newlines/unicode can't break anything.
        var person = new Person
        {
            Name = "O'Brien \"The Great\"\n☃ — Москва\\drop",
            Email = "weird+test@example.com",
            IsActive = true,
            CreatedAt = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc),
            Score = -123,
            Status = Status.Inactive,
        };

        await using (var writer = NewContext())
            await writer.People.YeetAsync(new[] { person });

        await using var reader = NewContext();
        var loaded = await reader.People.AsNoTracking().SingleAsync();
        Assert.Equal(person.Name, loaded.Name);
        Assert.Equal(person.Email, loaded.Email);
        Assert.Equal(person.Score, loaded.Score);
        Assert.Equal(person.Status, loaded.Status);
    }

    [Fact]
    public async Task Committed_ambient_transaction_persists()
    {
        await using (var writer = NewContext())
        await using (var tx = await writer.Database.BeginTransactionAsync())
        {
            await writer.People.YeetAsync(MakePeople(15));
            await tx.CommitAsync();
        }

        await using var reader = NewContext();
        Assert.Equal(15, await reader.People.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task App_assigned_keys_persist_exact_values()
    {
        var items = new[]
        {
            new Item { Id = 100, Code = "A", Quantity = 1 },
            new Item { Id = 200, Code = "B", Quantity = 2 },
            new Item { Id = 300, Code = "C", Quantity = 3 },
        };

        await using (var writer = NewContext())
            await writer.Items.YeetAsync(items);

        await using var reader = NewContext();
        var stored = await reader.Items.AsNoTracking()
            .OrderBy(i => i.Id)
            .ToListAsync();

        Assert.Equal(3, stored.Count);
        Assert.Equal(new[] { 100, 200, 300 }, stored.Select(i => i.Id));
        Assert.Equal(new[] { "A", "B", "C" }, stored.Select(i => i.Code));
        Assert.Equal(new[] { 1, 2, 3 }, stored.Select(i => i.Quantity));
    }

    [Fact]
    public async Task Nulls_and_values_persist_correctly_across_many_rows()
    {
        await using (var writer = NewContext())
            await writer.People.YeetAsync(MakePeople(300));

        await using var reader = NewContext();
        var nullScores = await reader.People.AsNoTracking().CountAsync(p => p.Score == null);
        var setScores = await reader.People.AsNoTracking().CountAsync(p => p.Score != null);

        // MakePeople sets Score=null when i % 3 == 0
        var expectedNulls = Enumerable.Range(0, 300).Count(i => i % 3 == 0);
        Assert.Equal(expectedNulls, nullScores);
        Assert.Equal(300 - expectedNulls, setScores);
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
