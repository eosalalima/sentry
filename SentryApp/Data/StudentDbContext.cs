using Microsoft.EntityFrameworkCore;

namespace SentryApp.Data;

public sealed class StudentDbContext : DbContext
{
    public StudentDbContext(DbContextOptions<StudentDbContext> options) : base(options) { }
}
