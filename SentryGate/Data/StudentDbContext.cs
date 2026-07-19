using Microsoft.EntityFrameworkCore;

namespace SentryGate.Data;

public sealed class StudentDbContext : DbContext
{
    public StudentDbContext(DbContextOptions<StudentDbContext> options) : base(options) { }
}
