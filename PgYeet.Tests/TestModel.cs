using Microsoft.EntityFrameworkCore;

namespace PgYeet.Tests;

public enum Status
{
    Active,
    Inactive
}

public sealed class Person
{
    public int Id { get; set; }                  // store-generated identity
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? Score { get; set; }              // nullable value type -> NullableWriter
    public Status Status { get; set; }           // value converter -> boxed write path
}

public sealed class Item
{
    public int Id { get; set; }                  // caller-assigned PK -> direct COPY path
    public string Code { get; set; } = "";
    public int Quantity { get; set; }
}

public sealed class Gadget
{
    public Guid Id { get; set; }                 // client-generated Guid PK -> copied as a plain column
    public string Label { get; set; } = "";
}

public readonly record struct OrderId(int Value);

public sealed class Order
{
    public OrderId Id { get; set; }              // strongly-typed identity PK (value converter)
    public string Reference { get; set; } = "";
}

public sealed class CompositeItem
{
    public int TenantId { get; set; }
    public int Id { get; set; }
    public string Label { get; set; } = "";
}

public sealed class CompositeGuidItem
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public string Label { get; set; } = "";
}

public sealed class BigOrder
{
    public OrderId Id { get; set; }
    public string Reference { get; set; } = "";
}

public sealed class OrdinalRow
{
    public int Id { get; set; }
    public string Value { get; set; } = "";
}

public class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<Person> People => Set<Person>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Gadget> Gadgets => Set<Gadget>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<CompositeItem> CompositeItems => Set<CompositeItem>();
    public DbSet<CompositeGuidItem> CompositeGuidItems => Set<CompositeGuidItem>();
    public DbSet<BigOrder> BigOrders => Set<BigOrder>();
    public DbSet<OrdinalRow> OrdinalRows => Set<OrdinalRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<OrdinalRow>(e =>
        {
            e.ToTable("ordinal_rows");
            e.Property(x => x.Value).HasColumnName("__ord");
        });
        b.Entity<Person>(e =>
        {
            e.ToTable("people");
            e.Property(p => p.Name).HasMaxLength(200);
            e.Property(p => p.Email).HasMaxLength(320);
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<Item>(e =>
        {
            e.ToTable("items");
            e.Property(i => i.Id).ValueGeneratedNever();   // not identity -> caller supplies the PK
            e.Property(i => i.Code).HasMaxLength(50);
        });

        b.Entity<Gadget>(e =>
        {
            e.ToTable("gadgets");
            e.Property(g => g.Label).HasMaxLength(50);
        });

        b.Entity<Order>(e =>
        {
            e.ToTable("orders");
            e.Property(o => o.Id)
                .HasConversion(id => id.Value, v => new OrderId(v))
                .UseIdentityByDefaultColumn();
            e.Property(o => o.Reference).HasMaxLength(50);
        });

        b.Entity<CompositeItem>(e =>
        {
            e.ToTable("composite_items");
            e.HasKey(x => new { x.TenantId, x.Id });
            e.Property(x => x.TenantId).ValueGeneratedNever();
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Label).HasMaxLength(50);
        });

        b.Entity<CompositeGuidItem>(e =>
        {
            e.ToTable("composite_guid_items");
            e.HasKey(x => new { x.TenantId, x.Id });
            // EF may mark client-generated Guid keys OnAdd even though the database has no default.
            // PgYeet must still copy the values supplied by the application.
            e.Property(x => x.TenantId).ValueGeneratedOnAdd();
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Label).HasMaxLength(50);
        });

        b.Entity<BigOrder>(e =>
        {
            e.ToTable("big_orders");
            e.Property(x => x.Id)
                .HasConversion<long>(id => id.Value, value => new OrderId(checked((int)value)))
                .UseIdentityByDefaultColumn();
            e.Property(x => x.Reference).HasMaxLength(50);
        });
    }
}
