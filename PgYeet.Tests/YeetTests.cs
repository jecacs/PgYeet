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

    [Fact]
    public async Task Client_generated_guid_pk_copies_caller_values()
    {
        await using var db = NewContext();
        var gadgets = Enumerable.Range(0, 20)
            .Select(i => new Gadget { Id = Guid.NewGuid(), Label = "g" + i })
            .ToArray();

        var inserted = await db.Gadgets.YeetAsync(gadgets);

        Assert.Equal(20, inserted);
        var stored = await db.Gadgets.AsNoTracking().ToListAsync();
        Assert.Equal(
            gadgets.Select(g => g.Id).OrderBy(x => x),
            stored.Select(g => g.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task Strongly_typed_identity_pk_writes_keys_back_through_converter()
    {
        await using var db = NewContext();
        var orders = Enumerable.Range(0, 30).Select(i => new Order { Reference = "ref-" + i }).ToArray();

        var inserted = await db.Orders.YeetAsync(orders);

        Assert.Equal(30, inserted);
        Assert.All(orders, o => Assert.True(o.Id.Value > 0));
        Assert.Equal(30, orders.Select(o => o.Id).Distinct().Count());

        // every written-back key addresses the row holding that entity's data
        var byId = orders.ToDictionary(o => o.Id.Value, o => o.Reference);
        var rows = await db.Orders.AsNoTracking().ToListAsync();
        Assert.Equal(30, rows.Count);
        Assert.All(rows, r => Assert.Equal(byId[r.Id.Value], r.Reference));
    }

    [Fact]
    public async Task NoKeys_path_streams_a_lazy_enumerable()
    {
        await using var db = NewContext();

        var inserted = await db.People.YeetAsync(LazyPeople(1_234), returnGeneratedKeys: false);

        Assert.Equal(1_234, inserted);
        Assert.Equal(1_234, await db.People.AsNoTracking().CountAsync());

        static IEnumerable<Person> LazyPeople(int n)
        {
            for (var i = 0; i < n; i++)
                yield return new Person
                {
                    Name = "s" + i, Email = $"s{i}@example.com", IsActive = true,
                    CreatedAt = DateTime.UtcNow, Status = Status.Active,
                };
        }
    }

    [Fact]
    public async Task Row_filtering_trigger_raises_clear_error_instead_of_wrong_keys()
    {
        await using var db = NewContext();
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE FUNCTION _pgyeet_skip() RETURNS trigger AS $$
            BEGIN
                IF NEW."Name" = 'skip-me' THEN RETURN NULL; END IF;
                RETURN NEW;
            END $$ LANGUAGE plpgsql;
            CREATE TRIGGER _pgyeet_skip BEFORE INSERT ON people FOR EACH ROW EXECUTE FUNCTION _pgyeet_skip();
            """);
        try
        {
            var people = MakePeople(5);
            people[2].Name = "skip-me";

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.People.YeetAsync(people));

            Assert.Contains("RETURNING", ex.Message);
            Assert.Equal(0, await db.People.AsNoTracking().CountAsync());   // own transaction rolled back
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER IF EXISTS _pgyeet_skip ON people; DROP FUNCTION IF EXISTS _pgyeet_skip();");
        }
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
