using Microsoft.EntityFrameworkCore;

namespace SentryGate.Data;

public sealed class AccessControlDbContext : DbContext
{
    public AccessControlDbContext(DbContextOptions<AccessControlDbContext> options) : base(options) { }
}
