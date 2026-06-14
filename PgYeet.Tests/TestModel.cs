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

public class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<Person> People => Set<Person>();
    public DbSet<Item> Items => Set<Item>();

    protected override void OnModelCreating(ModelBuilder b)
    {
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
    }
}
