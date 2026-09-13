using Microsoft.EntityFrameworkCore;
using PgYeet;

var exportedTypes = typeof(PgYeetExtensions).Assembly
    .GetExportedTypes()
    .Select(type => type.FullName)
    .ToArray();

if (exportedTypes is not ["PgYeet.PgYeetExtensions"])
    throw new InvalidOperationException($"Unexpected public API: {string.Join(", ", exportedTypes)}");

var options = new DbContextOptionsBuilder<SmokeDbContext>()
    .UseNpgsql(Environment.GetEnvironmentVariable("PGYEET_SMOKE_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Set PGYEET_SMOKE_CONNECTION_STRING to a disposable PostgreSQL database."))
    .Options;

await using var db = new SmokeDbContext(options);
try
{
    await db.Set<SmokeBase>().YeetAsync(new[] { new SmokeBase() });
    throw new InvalidOperationException("The TPH mapping guard did not execute.");
}
catch (NotSupportedException exception) when (exception.Message.Contains("TPH", StringComparison.Ordinal))
{
}

await db.Database.EnsureCreatedAsync();
var marker = Guid.NewGuid().ToString("N");
var rows = new[] { new SmokeRow { Value = marker }, new SmokeRow { Value = marker, Rank = 42 } };
if (await db.Set<SmokeRow>().YeetAsync(rows, ct: CancellationToken.None) != 2)
    throw new InvalidOperationException("Unexpected generated-key insert count.");

await using (var reader = new SmokeDbContext(options))
{
    foreach (var row in rows)
    {
        var stored = await reader.Set<SmokeRow>().AsNoTracking().SingleAsync(x => x.Id == row.Id);
        if (stored.Value != row.Value || stored.Rank != row.Rank)
            throw new InvalidOperationException("Generated keys do not address the inserted values.");
    }
}

var direct = new SmokeRow { Value = marker };
if (await db.Set<SmokeRow>().YeetAsync(new[] { direct }, returnGeneratedKeys: false) != 1 || direct.Id != 0)
    throw new InvalidOperationException("Direct COPY did not preserve the no-key contract.");

await using (var transaction = await db.Database.BeginTransactionAsync())
{
    await db.Set<SmokeRow>().YeetAsync(new[] { new SmokeRow { Value = marker } });
    await transaction.RollbackAsync();
}

await using (var reader = new SmokeDbContext(options))
{
    if (await reader.Set<SmokeRow>().CountAsync(x => x.Value == marker) != 3)
        throw new InvalidOperationException("Packed package did not preserve transaction rollback.");
}

Console.WriteLine(
    $"PgYeet package PostgreSQL smoke passed on {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}.");

internal sealed class SmokeRow
{
    public int Id { get; set; }
    public string Value { get; set; } = "";
    public int? Rank { get; set; }
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
        modelBuilder.Entity<SmokeRow>().Property(x => x.Value).HasColumnName("__ord");
    }
}
