using Microsoft.EntityFrameworkCore;
using SentryApp.Data.Query;

namespace SentryApp.Data;

public sealed class AccessControlDbContext : DbContext
{
    public AccessControlDbContext(DbContextOptions<AccessControlDbContext> options) : base(options) { }

    // Keyless row used by our polling SQL
    public DbSet<TurnstileLogRow> TurnstileLogRows => Set<TurnstileLogRow>();
    public DbSet<ZkDevice> ZkDevices => Set<ZkDevice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TurnstileLogRow>(b =>
        {
            b.HasNoKey();
            b.ToView(null); // important: tells EF this isn't a real table/view for migrations
        });

        modelBuilder.Entity<ZkDevice>(b =>
        {
            b.HasNoKey();
            b.ToTable("ZKDevices");
        });
    }
}
