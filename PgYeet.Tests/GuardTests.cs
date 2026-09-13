using Microsoft.EntityFrameworkCore;
using Xunit;

namespace PgYeet.Tests;

#region unsupported-mapping model

public class Animal
{
    public int Id { get; set; }
    public string? Name { get; set; }
}

public sealed class Dog : Animal
{
    public bool GoodBoy { get; set; }
}

public sealed class OwnedOwner
{
    public int Id { get; set; }
    public Address Home { get; set; } = new();
}

public sealed class Address
{
    public string City { get; set; } = "";
}

public sealed class ComplexOwner
{
    public int Id { get; set; }
    public Money Price { get; set; }
}

public struct Money
{
    public decimal Amount { get; set; }
}

public sealed class ShadowOwner
{
    public int Id { get; set; }
}

public sealed class FieldOnlyRow
{
    public int Id { get; set; }
    public string? Hidden;
}

public sealed class ReadOnlyIdentityRow
{
    public int Id { get; }
    public string? Value { get; set; }
}

public sealed class UuidDefaultPk
{
    public Guid Id { get; set; }
    public string? Tag { get; set; }
}

public sealed class HiLoPk
{
    public int Id { get; set; }
    public string? Tag { get; set; }
}

public sealed class CompositeGeneratedPk
{
    public int TenantId { get; set; }
    public int Id { get; set; }
    public string? Tag { get; set; }
}

public sealed class IdentityOnly
{
    public int Id { get; set; }
}

public sealed class NonKeyIdentity
{
    public int Id { get; set; }
    public int SequenceValue { get; set; }
}

public class TpcVehicle
{
    public int Id { get; set; }
    public string? Name { get; set; }
}

public sealed class TpcCar : TpcVehicle
{
    public int DoorCount { get; set; }
}

public sealed class GuardsDbContext : DbContext
{
    // The guards fire while the COPY mapping is built, before any connection is opened,
    // so a placeholder connection string is enough — no database is touched.
    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseNpgsql("Host=localhost;Database=_pgyeet_guards;Username=x;Password=x");

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Animal>();
        b.Entity<Dog>();                                                         // TPH
        b.Entity<OwnedOwner>().OwnsOne(o => o.Home);                             // owned type
        b.Entity<ComplexOwner>().ComplexProperty(o => o.Price);                  // EF8 complex type
        b.Entity<ShadowOwner>(e => e.Property<string>("Hidden").IsRequired());   // required shadow
        b.Entity<FieldOnlyRow>().Property<string>(nameof(FieldOnlyRow.Hidden));
        b.Entity<ReadOnlyIdentityRow>().HasKey(x => x.Id);
        b.Entity<UuidDefaultPk>(e => e.Property(u => u.Id).HasDefaultValueSql("gen_random_uuid()"));
        b.Entity<HiLoPk>(e => e.Property(h => h.Id).UseHiLo());
        b.Entity<CompositeGeneratedPk>(e =>
        {
            e.HasKey(x => new { x.TenantId, x.Id });
            e.Property(x => x.TenantId).ValueGeneratedNever();
            e.Property(x => x.Id).UseIdentityByDefaultColumn();
        });
        b.Entity<IdentityOnly>();
        b.Entity<NonKeyIdentity>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.SequenceValue).UseIdentityByDefaultColumn();
        });
        b.Entity<TpcVehicle>().UseTpcMappingStrategy();
        b.Entity<TpcCar>();
    }
}

#endregion

/// <summary>
/// Mappings PgYeet cannot insert completely must fail fast with a clear exception —
/// never silently write rows with missing columns.
/// </summary>
public sealed class GuardTests
{
    [Fact]
    public async Task Nullable_field_only_property_is_rejected_instead_of_losing_data()
    {
        await using var db = new GuardsDbContext();
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => db.Set<FieldOnlyRow>().YeetAsync(new[] { new FieldOnlyRow { Hidden = "keep me" } }));
        Assert.Contains("field-only", ex.Message);
    }

    [Fact]
    public async Task Read_only_identity_is_rejected_before_inserting()
    {
        await using var db = new GuardsDbContext();
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => db.Set<ReadOnlyIdentityRow>().YeetAsync(new[] { new ReadOnlyIdentityRow() }));
        Assert.Contains("getter and setter", ex.Message);
    }

    [Fact]
    public async Task Cancelled_input_is_not_enumerated_even_when_empty()
    {
        await using var db = new GuardsDbContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => db.Set<Animal>().YeetAsync(Array.Empty<Animal>(), ct: cancellation.Token));
    }

    [Fact]
    public async Task Cancellation_during_buffering_stops_before_database_io()
    {
        using var cancellation = new CancellationTokenSource();
        IEnumerable<Person> Rows()
        {
            cancellation.Cancel();
            yield return new Person();
            throw new InvalidOperationException("Enumeration continued after cancellation.");
        }

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=unused;Username=unused")
            .Options;
        await using var supported = new TestDbContext(options);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => supported.People.YeetAsync(Rows(), ct: cancellation.Token));
    }

    [Fact]
    public async Task Tph_hierarchy_is_rejected()
    {
        await using var db = new GuardsDbContext();
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => db.Set<Animal>().YeetAsync(new[] { new Animal() }));
        Assert.Contains("TPH", ex.Message);
    }

    [Fact]
    public async Task Owned_type_is_rejected()
    {
        await using var db = new GuardsDbContext();
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => db.Set<OwnedOwner>().YeetAsync(new[] { new OwnedOwner() }));
        Assert.Contains("owned", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Complex_type_is_rejected()
    {
        await using var db = new GuardsDbContext();
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => db.Set<ComplexOwner>().YeetAsync(new[] { new ComplexOwner() }));
        Assert.Contains("complex", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Required_shadow_property_is_rejected()
    {
        await using var db = new GuardsDbContext();
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => db.Set<ShadowOwner>().YeetAsync(new[] { new ShadowOwner() }));
        Assert.Contains("shadow", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Uuid_default_pk_is_rejected()
    {
        await using var db = new GuardsDbContext();
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => db.Set<UuidDefaultPk>().YeetAsync(new[] { new UuidDefaultPk() }));
        Assert.Contains("identity/serial", ex.Message);
    }

    [Fact]
    public async Task HiLo_pk_is_rejected()
    {
        await using var db = new GuardsDbContext();
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => db.Set<HiLoPk>().YeetAsync(new[] { new HiLoPk() }));
        Assert.Contains("identity/serial", ex.Message);
    }

    [Fact]
    public async Task Store_generated_composite_pk_is_rejected()
    {
        await using var db = new GuardsDbContext();
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => db.Set<CompositeGeneratedPk>().YeetAsync(new[] { new CompositeGeneratedPk() }));
        Assert.Contains("composite primary key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Entity_with_only_generated_columns_is_rejected()
    {
        await using var db = new GuardsDbContext();
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => db.Set<IdentityOnly>().YeetAsync(new[] { new IdentityOnly() }));
        Assert.Contains("no insertable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Non_key_identity_is_rejected()
    {
        await using var db = new GuardsDbContext();
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => db.Set<NonKeyIdentity>().YeetAsync(new[] { new NonKeyIdentity() }));
        Assert.Contains("non-primary-key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tpc_hierarchy_is_rejected()
    {
        await using var db = new GuardsDbContext();
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => db.Set<TpcVehicle>().YeetAsync(new[] { new TpcVehicle() }));
        Assert.Contains("inheritance hierarchy", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
