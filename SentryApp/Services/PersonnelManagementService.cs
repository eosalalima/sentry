using Microsoft.EntityFrameworkCore;
using SentryApp.Data;
using SentryApp.Data.Query;

namespace SentryApp.Services;

public sealed class PersonnelManagementService
{
    public const int MobileNumberMaxLength = 50;

    private readonly IDbContextFactory<StudentDbContext> _studentDbFactory;
    private readonly IDbContextFactory<StaffDbContext> _staffDbFactory;
    private readonly ILogger<PersonnelManagementService> _logger;

    public PersonnelManagementService(
        IDbContextFactory<StudentDbContext> studentDbFactory,
        IDbContextFactory<StaffDbContext> staffDbFactory,
        ILogger<PersonnelManagementService> logger)
    {
        _studentDbFactory = studentDbFactory;
        _staffDbFactory = staffDbFactory;
        _logger = logger;
    }

    public async Task<PersonnelPage> GetPersonnelAsync(
        PersonnelType type,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var pattern = $"%{EscapeLike(search?.Trim() ?? string.Empty)}%";
        var offset = (page - 1) * pageSize;

        try
        {
            return type == PersonnelType.Student
                ? await GetStudentsAsync(pattern, offset, pageSize, cancellationToken)
                : await GetStaffAsync(pattern, offset, pageSize, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load {PersonnelType} personnel.", type);
            throw;
        }
    }

    public async Task UpdateMobileNumberAsync(
        PersonnelType type,
        string idNumber,
        string? mobileNumber,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idNumber))
            throw new ArgumentException("A personnel identifier is required.", nameof(idNumber));

        var normalizedMobile = string.IsNullOrWhiteSpace(mobileNumber) ? null : mobileNumber.Trim();
        if (normalizedMobile?.Length > MobileNumberMaxLength)
            throw new ArgumentException($"Mobile number cannot exceed {MobileNumberMaxLength} characters.", nameof(mobileNumber));

        try
        {
            var affectedRows = type == PersonnelType.Student
                ? await UpdateStudentAsync(idNumber, normalizedMobile, cancellationToken)
                : await UpdateStaffAsync(idNumber, normalizedMobile, cancellationToken);

            if (affectedRows != 1)
                throw new InvalidOperationException("The personnel record was not found or was not uniquely identified.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update the mobile number for {PersonnelType} personnel {IdNumber}.", type, idNumber);
            throw;
        }
    }

    private async Task<PersonnelPage> GetStudentsAsync(string pattern, int offset, int pageSize, CancellationToken ct)
    {
        await using var db = await _studentDbFactory.CreateDbContextAsync(ct);
        var count = await db.Database.SqlQueryRaw<int>($"""
            SELECT COUNT(*) AS [Value]
            FROM [dbo].[MyDataTable]
            WHERE {StudentSearchClause}
            """, pattern).SingleAsync(ct);
        var records = await db.Database.SqlQueryRaw<PersonnelManagementRecord>($"""
            SELECT Field01 AS IdNumber, Field02 AS LastName, Field03 AS FirstName,
                   Field04 AS MiddleInitial, Field06 AS Classification, Field13 AS MobileNumber
            FROM [dbo].[MyDataTable]
            WHERE {StudentSearchClause}
            ORDER BY Field02 ASC, Field03 ASC, Field04 ASC
            OFFSET {{1}} ROWS FETCH NEXT {{2}} ROWS ONLY
            """, pattern, offset, pageSize).ToListAsync(ct);
        return new PersonnelPage(records, count);
    }

    private async Task<PersonnelPage> GetStaffAsync(string pattern, int offset, int pageSize, CancellationToken ct)
    {
        await using var db = await _staffDbFactory.CreateDbContextAsync(ct);
        var count = await db.Database.SqlQueryRaw<int>($"""
            SELECT COUNT(*) AS [Value]
            FROM [dbo].[MyDataTable]
            WHERE {StaffSearchClause}
            """, pattern).SingleAsync(ct);
        var records = await db.Database.SqlQueryRaw<PersonnelManagementRecord>($"""
            SELECT Field01 AS IdNumber, Field02 AS LastName, Field03 AS FirstName,
                   Field04 AS MiddleInitial, Field05 AS Classification, Field13 AS MobileNumber
            FROM [dbo].[MyDataTable]
            WHERE {StaffSearchClause}
            ORDER BY Field02 ASC, Field03 ASC, Field04 ASC
            OFFSET {{1}} ROWS FETCH NEXT {{2}} ROWS ONLY
            """, pattern, offset, pageSize).ToListAsync(ct);
        return new PersonnelPage(records, count);
    }

    private async Task<int> UpdateStudentAsync(string idNumber, string? mobileNumber, CancellationToken ct)
    {
        await using var db = await _studentDbFactory.CreateDbContextAsync(ct);
        return await db.Database.ExecuteSqlAsync(
            $"UPDATE [dbo].[MyDataTable] SET Field13 = {mobileNumber} WHERE Field01 = {idNumber}", ct);
    }

    private async Task<int> UpdateStaffAsync(string idNumber, string? mobileNumber, CancellationToken ct)
    {
        await using var db = await _staffDbFactory.CreateDbContextAsync(ct);
        return await db.Database.ExecuteSqlAsync(
            $"UPDATE [dbo].[MyDataTable] SET Field13 = {mobileNumber} WHERE Field01 = {idNumber}", ct);
    }

    private static string EscapeLike(string value) => value
        .Replace("~", "~~", StringComparison.Ordinal)
        .Replace("%", "~%", StringComparison.Ordinal)
        .Replace("_", "~_", StringComparison.Ordinal)
        .Replace("[", "~[", StringComparison.Ordinal);

    private const string StudentSearchClause = """
        (Field01 LIKE {0} ESCAPE '~' OR Field02 LIKE {0} ESCAPE '~' OR Field03 LIKE {0} ESCAPE '~'
         OR Field04 LIKE {0} ESCAPE '~' OR Field06 LIKE {0} ESCAPE '~' OR Field13 LIKE {0} ESCAPE '~')
        """;

    private const string StaffSearchClause = """
        (Field01 LIKE {0} ESCAPE '~' OR Field02 LIKE {0} ESCAPE '~' OR Field03 LIKE {0} ESCAPE '~'
         OR Field04 LIKE {0} ESCAPE '~' OR Field05 LIKE {0} ESCAPE '~' OR Field13 LIKE {0} ESCAPE '~')
        """;
}
