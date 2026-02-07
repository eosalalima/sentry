using Microsoft.EntityFrameworkCore;

namespace SentryGate.Data;

public sealed class StaffDbContext : DbContext
{
    public StaffDbContext(DbContextOptions<StaffDbContext> options) : base(options) { }
}
