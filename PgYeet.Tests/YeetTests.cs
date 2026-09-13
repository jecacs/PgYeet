using Microsoft.EntityFrameworkCore;
using Npgsql;
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
            Name = "Ada",
            Email = "ada@example.com",
            IsActive = true,
            CreatedAt = createdAt,
            Score = null,
            Status = Status.Inactive,
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
                    Name = "s" + i,
                    Email = $"s{i}@example.com",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    Status = Status.Active,
                };
        }
    }

    [Fact]
    public async Task Fully_app_assigned_composite_key_is_copied()
    {
        await using var db = NewContext();
        var items = new[]
        {
            new CompositeItem { TenantId = 7, Id = 1, Label = "first" },
            new CompositeItem { TenantId = 7, Id = 2, Label = "second" },
        };

        var inserted = await db.CompositeItems.YeetAsync(items);

        Assert.Equal(2, inserted);
        Assert.Equal(2, await db.CompositeItems.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Client_generated_composite_guid_key_is_copied()
    {
        await using var db = NewContext();
        var item = new CompositeGuidItem
        {
            TenantId = Guid.NewGuid(),
            Id = Guid.NewGuid(),
            Label = "client-generated",
        };

        var inserted = await db.CompositeGuidItems.YeetAsync(new[] { item });

        Assert.Equal(1, inserted);
        var stored = await db.CompositeGuidItems.AsNoTracking().SingleAsync();
        Assert.Equal(item.TenantId, stored.TenantId);
        Assert.Equal(item.Id, stored.Id);
    }

    [Fact]
    public async Task Yeet_does_not_change_added_entity_state()
    {
        await using var db = NewContext();
        var person = MakePeople(1)[0];
        db.People.Add(person);

        await db.People.YeetAsync(new[] { person });

        Assert.Equal(EntityState.Added, db.Entry(person).State);
        Assert.True(person.Id > 0);
        Assert.Equal(1, await db.People.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Null_element_aborts_direct_copy_atomically()
    {
        await using var db = NewContext();
        var people = new[] { MakePeople(1)[0], null! };

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => db.People.YeetAsync(people, returnGeneratedKeys: false));

        Assert.Equal("entities", ex.ParamName);
        Assert.Equal(0, await db.People.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Direct_copy_returns_the_number_of_rows_accepted_by_a_trigger()
    {
        await using var db = NewContext();
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE FUNCTION _pgyeet_direct_skip() RETURNS trigger AS $$
            BEGIN
                IF NEW."Name" = 'skip-me' THEN RETURN NULL; END IF;
                RETURN NEW;
            END $$ LANGUAGE plpgsql;
            CREATE TRIGGER _pgyeet_direct_skip BEFORE INSERT ON people
            FOR EACH ROW EXECUTE FUNCTION _pgyeet_direct_skip();
            """);

        try
        {
            var people = MakePeople(5);
            people[2].Name = "skip-me";

            var inserted = await db.People.YeetAsync(people, returnGeneratedKeys: false);

            Assert.Equal(4, inserted);
            Assert.Equal(4, await db.People.AsNoTracking().CountAsync());
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER IF EXISTS _pgyeet_direct_skip ON people; " +
                "DROP FUNCTION IF EXISTS _pgyeet_direct_skip();");
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

    [Fact]
    public async Task Duplicate_object_reference_is_rejected_before_inserting()
    {
        await using var db = NewContext();
        var person = MakePeople(1)[0];

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => db.People.YeetAsync(new[] { person, person }));

        Assert.Equal("entities", ex.ParamName);
        Assert.Contains("same object reference", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, person.Id);
        Assert.Equal(0, await db.People.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Failed_own_transaction_restores_original_key_values()
    {
        await using var db = NewContext();
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE people ADD CONSTRAINT uq_pgyeet_deferred_email " +
            "UNIQUE (\"Email\") DEFERRABLE INITIALLY DEFERRED;");

        try
        {
            var people = MakePeople(2);
            people[0].Email = "duplicate@example.com";
            people[1].Email = "duplicate@example.com";

            await Assert.ThrowsAsync<PostgresException>(() => db.People.YeetAsync(people));

            Assert.All(people, person => Assert.Equal(0, person.Id));
            Assert.Equal(0, await db.People.AsNoTracking().CountAsync());
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE people DROP CONSTRAINT IF EXISTS uq_pgyeet_deferred_email;");
        }
    }

    [Fact]
    public async Task Cyclic_database_sequence_is_rejected_before_inserting()
    {
        await using var db = NewContext();
        await db.Database.ExecuteSqlRawAsync("ALTER SEQUENCE \"people_Id_seq\" CYCLE;");

        try
        {
            var person = MakePeople(1)[0];
            var ex = await Assert.ThrowsAsync<NotSupportedException>(
                () => db.People.YeetAsync(new[] { person }));

            Assert.Contains("cyclic sequence", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, person.Id);
            Assert.Equal(0, await db.People.AsNoTracking().CountAsync());
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER SEQUENCE \"people_Id_seq\" NO CYCLE;");
        }
    }

    [Fact]
    public async Task Descending_database_sequence_is_rejected_before_inserting()
    {
        await using var db = NewContext();
        await db.Database.ExecuteSqlRawAsync(
            "ALTER SEQUENCE \"people_Id_seq\" INCREMENT BY -1 MINVALUE -2147483648 " +
            "MAXVALUE 2147483647 RESTART WITH -1;");

        try
        {
            var person = MakePeople(1)[0];
            var ex = await Assert.ThrowsAsync<NotSupportedException>(
                () => db.People.YeetAsync(new[] { person }));

            Assert.Contains("positive increment", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, person.Id);
            Assert.Equal(0, await db.People.AsNoTracking().CountAsync());
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER SEQUENCE \"people_Id_seq\" INCREMENT BY 1 NO MINVALUE NO MAXVALUE " +
                "NO CYCLE RESTART WITH 1;");
        }
    }

    [Fact]
    public async Task Generated_key_conversion_overflow_rolls_back_without_mutating_objects()
    {
        await using var db = NewContext();
        await db.Database.ExecuteSqlRawAsync(
            "ALTER SEQUENCE \"big_orders_Id_seq\" RESTART WITH 2147483648;");

        var order = new BigOrder { Reference = "overflow" };

        await Assert.ThrowsAsync<OverflowException>(() => db.BigOrders.YeetAsync(new[] { order }));

        Assert.Equal(0, order.Id.Value);
        Assert.Equal(0, await db.BigOrders.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task User_column_named_ord_round_trips_with_generated_keys()
    {
        await using var db = NewContext();
        var rows = new[] { new OrdinalRow { Value = "second" }, new OrdinalRow { Value = "first" } };
        Assert.Equal(2, await db.OrdinalRows.YeetAsync(rows));

        await using var reader = NewContext();
        foreach (var row in rows)
            Assert.Equal(row.Value, (await reader.OrdinalRows.AsNoTracking().SingleAsync(x => x.Id == row.Id)).Value);
    }

    [Fact]
    public async Task Failed_write_back_rolls_back_only_its_savepoint()
    {
        await using var db = NewContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.People.YeetAsync(MakePeople(1));
        await db.Database.ExecuteSqlRawAsync("ALTER SEQUENCE \"big_orders_Id_seq\" RESTART WITH 2147483648;");

        var order = new BigOrder { Reference = "overflow" };
        await Assert.ThrowsAsync<OverflowException>(() => db.BigOrders.YeetAsync(new[] { order }));

        Assert.Equal(0, order.Id.Value);
        Assert.Equal(0, await db.BigOrders.CountAsync());
        Assert.Equal(1, await db.People.CountAsync());
        await transaction.CommitAsync();

        await using var reader = NewContext();
        Assert.Equal(0, await reader.BigOrders.CountAsync());
        Assert.Equal(1, await reader.People.CountAsync());
    }

    [Fact]
    public async Task Database_error_leaves_callers_transaction_usable()
    {
        await using var db = NewContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.People.YeetAsync(MakePeople(1));
        var invalid = MakePeople(1);
        invalid[0].Name = new string('x', 201);

        await Assert.ThrowsAsync<PostgresException>(() => db.People.YeetAsync(invalid));

        Assert.Equal(0, invalid[0].Id);
        Assert.Equal(1, await db.People.CountAsync());
        await db.People.YeetAsync(MakePeople(1));
        await transaction.CommitAsync();
        await using var reader = NewContext();
        Assert.Equal(2, await reader.People.CountAsync());
    }

    [Fact]
    public async Task Staging_tables_do_not_accumulate_inside_callers_transaction()
    {
        await using var db = NewContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        for (var i = 0; i < 3; i++)
            await db.People.YeetAsync(MakePeople(1));

        var count = await db.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS \"Value\" FROM pg_class " +
            "WHERE relnamespace = pg_my_temp_schema() AND starts_with(relname, '_pgyeet_')").SingleAsync();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Enumeration_failure_aborts_direct_copy_and_preserves_open_connection()
    {
        await using var db = NewContext();
        await db.Database.OpenConnectionAsync();
        var expected = new InvalidOperationException("Input failed.");
        IEnumerable<Person> Rows()
        {
            yield return MakePeople(1)[0];
            throw expected;
        }

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.People.YeetAsync(Rows(), returnGeneratedKeys: false));
        Assert.Same(expected, actual);
        Assert.Equal(System.Data.ConnectionState.Open, db.Database.GetDbConnection().State);
        Assert.Equal(0, await db.People.CountAsync());
        Assert.Equal(1, await db.People.YeetAsync(MakePeople(1)));
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
