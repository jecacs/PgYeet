using Microsoft.EntityFrameworkCore;
using PgYeet;

var exportedTypes = typeof(PgYeetExtensions).Assembly
    .GetExportedTypes()
    .Select(type => type.FullName)
    .ToArray();

if (exportedTypes is not ["PgYeet.PgYeetExtensions"])
    throw new InvalidOperationException($"Unexpected public API: {string.Join(", ", exportedTypes)}");

var options = new DbContextOptionsBuilder<SmokeDbContext>()
    .UseNpgsql("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused")
    .Options;

await using var db = new SmokeDbContext(options);
try
{
    await db.Set<SmokeBase>().YeetAsync(new[] { new SmokeBase() });
    throw new InvalidOperationException("The TPH mapping guard did not execute.");
}
catch (NotSupportedException exception) when (exception.Message.Contains("TPH", StringComparison.Ordinal))
{
    Console.WriteLine(
        $"PgYeet package smoke passed on {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}.");
}

internal class SmokeBase
{
    public int Id { get; set; }
}

internal sealed class SmokeDerived : SmokeBase;

internal sealed class SmokeDbContext(DbContextOptions<SmokeDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SmokeBase>();
        modelBuilder.Entity<SmokeDerived>();
    }
}
