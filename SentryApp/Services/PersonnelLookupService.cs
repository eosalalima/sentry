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

    public async Task<PersonnelLookupResult?> GetPersonnelAsync(string? accessNumber, CancellationToken ct)
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
SELECT Field13 AS MobileNumber, Field05 AS Classification
FROM [dbo].[MyDataTable]
WHERE Field15 = {0}", accessNumber).ToListAsync(ct);

            var studentTask = studentDb.Database.SqlQueryRaw<PersonnelUnionRow>(@"
SELECT [Field10] AS MobileNumber, Field06 AS Classification
FROM [dbo].[MyDataTable]
WHERE Field15 = {0}", accessNumber).ToListAsync(ct);

            await Task.WhenAll(staffTask, studentTask);

            var staff = staffTask.Result.FirstOrDefault();
            if (staff is not null)
                return new PersonnelLookupResult(staff.MobileNumber, staff.Classification, PersonnelKind.Staff);

            var student = studentTask.Result.FirstOrDefault();
            return student is null
                ? null
                : new PersonnelLookupResult(student.MobileNumber, student.Classification, PersonnelKind.Student);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to lookup mobile number for access number {AccessNumber}.", accessNumber);
            return null;
        }
    }
}

public sealed record PersonnelLookupResult(
    string? MobileNumber,
    string? Classification,
    PersonnelKind Kind);

public enum PersonnelKind
{
    Student,
    Staff
}
