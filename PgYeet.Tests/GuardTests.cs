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
        b.Entity<UuidDefaultPk>(e => e.Property(u => u.Id).HasDefaultValueSql("gen_random_uuid()"));
        b.Entity<HiLoPk>(e => e.Property(h => h.Id).UseHiLo());
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
}
