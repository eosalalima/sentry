using Microsoft.EntityFrameworkCore;
using SentryApp.Data;
using SentryApp.Data.Query;

namespace SentryApp.Services;

public sealed class PersonnelLookupService
{
    private readonly IDbContextFactory<StaffDbContext> _staffDbFactory;
    private readonly IDbContextFactory<StudentDbContext> _studentDbFactory;
    private readonly ILogger<PersonnelLookupService> _logger;

    public PersonnelLookupService(
        IDbContextFactory<StaffDbContext> staffDbFactory,
        IDbContextFactory<StudentDbContext> studentDbFactory,
        ILogger<PersonnelLookupService> logger)
    {
        _staffDbFactory = staffDbFactory;
        _studentDbFactory = studentDbFactory;
        _logger = logger;
    }

    public async Task<string?> GetMobileNumberAsync(string? accessNumber, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessNumber))
        {
            return null;
        }

        try
        {
            await using var staffDb = await _staffDbFactory.CreateDbContextAsync(ct);
            await using var studentDb = await _studentDbFactory.CreateDbContextAsync(ct);

            var staffTask = staffDb.Database.SqlQueryRaw<PersonnelUnionRow>(@"
SELECT Field10, Field15
FROM [dbo].[MyDataTable]
WHERE Field15 = {0}", accessNumber).ToListAsync(ct);

            var studentTask = studentDb.Database.SqlQueryRaw<PersonnelUnionRow>(@"
SELECT Field10, Field15
FROM [dbo].[MyDataTable]
WHERE Field15 = {0}", accessNumber).ToListAsync(ct);

            await Task.WhenAll(staffTask, studentTask);

            return staffTask.Result
                .Concat(studentTask.Result)
                .Select(row => row.Field10)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to lookup mobile number for access number {AccessNumber}.", accessNumber);
            return null;
        }
    }
}
