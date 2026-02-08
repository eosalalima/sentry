using Microsoft.EntityFrameworkCore;

namespace SentryApp.Data;

public sealed class StaffDbContext : DbContext
{
    public StaffDbContext(DbContextOptions<StaffDbContext> options) : base(options) { }
}
