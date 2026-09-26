using Microsoft.EntityFrameworkCore;
using SentryApp.Data;
using SentryApp.Data.Query;

namespace SentryApp.Services;

public sealed class PersonnelManagementService
{
    public const int SmsContactNumberMaxLength = 50;

    private readonly IDbContextFactory<AccessControlDbContext> _dbFactory;
    private readonly ILogger<PersonnelManagementService> _logger;

    public PersonnelManagementService(
        IDbContextFactory<AccessControlDbContext> dbFactory,
        ILogger<PersonnelManagementService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<PersonnelPage> GetPersonnelAsync(
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
            return await GetPageAsync(pattern, offset, pageSize, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load personnel.");
            throw;
        }
    }

    public async Task UpdateSmsContactNumberAsync(
        string personnelNo,
        string? smsContactNumber,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(personnelNo))
            throw new ArgumentException("A personnel identifier is required.", nameof(personnelNo));

        var normalizedNumber = string.IsNullOrWhiteSpace(smsContactNumber) ? null : smsContactNumber.Trim();
        if (normalizedNumber?.Length > SmsContactNumberMaxLength)
            throw new ArgumentException($"SMS contact number cannot exceed {SmsContactNumberMaxLength} characters.", nameof(smsContactNumber));

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var affectedRows = await db.Database.ExecuteSqlAsync(
                $"UPDATE [dbo].[Personnels] SET [SmsContactNumber] = {normalizedNumber} WHERE [PersonnelNo] = {personnelNo} AND [IsDeleted] = 0",
                cancellationToken);

            if (affectedRows != 1)
                throw new InvalidOperationException("The personnel record was not found or was not uniquely identified.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update the SMS contact number for personnel {PersonnelNo}.", personnelNo);
            throw;
        }
    }

    private async Task<PersonnelPage> GetPageAsync(string pattern, int offset, int pageSize, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var count = await db.Database.SqlQueryRaw<int>($$"""
            SELECT COUNT(*) AS [Value]
            FROM [dbo].[Personnels] p
            {{MobileNumberLookup}}
            WHERE [IsDeleted] = 0 AND {{SearchClause}}
            """, pattern).SingleAsync(ct);
        var records = await db.Database.SqlQueryRaw<PersonnelManagementRecord>($$"""
            SELECT COALESCE(CAST([PersonnelNo] AS nvarchar(max)), N'') AS [PersonnelNo],
                   COALESCE(CAST([LastName] AS nvarchar(max)), N'') AS [LastName],
                   COALESCE(CAST([FirstName] AS nvarchar(max)), N'') AS [FirstName],
                   CAST([PhotoId] AS nvarchar(max)) AS [PhotoId],
                   COALESCE(
                       NULLIF(LTRIM(RTRIM(CAST(p.[SmsContactNumber] AS nvarchar(max)))), N''),
                       directory.[MobileNumber]
                   ) AS [SmsContactNumber]
            FROM [dbo].[Personnels] p
            {{MobileNumberLookup}}
            WHERE [IsDeleted] = 0 AND {{SearchClause}}
            ORDER BY [LastName] ASC, [FirstName] ASC, [PersonnelNo] ASC
            OFFSET {1} ROWS FETCH NEXT {2} ROWS ONLY
            """, pattern, offset, pageSize).ToListAsync(ct);
        return new PersonnelPage(records, count);
    }

    private static string EscapeLike(string value) => value
        .Replace("~", "~~", StringComparison.Ordinal)
        .Replace("%", "~%", StringComparison.Ordinal)
        .Replace("_", "~_", StringComparison.Ordinal)
        .Replace("[", "~[", StringComparison.Ordinal);

    private const string SearchClause = """
        (CAST(p.[PersonnelNo] AS nvarchar(max)) LIKE {0} ESCAPE '~'
         OR CAST(p.[LastName] AS nvarchar(max)) LIKE {0} ESCAPE '~'
         OR CAST(p.[FirstName] AS nvarchar(max)) LIKE {0} ESCAPE '~'
         OR COALESCE(
                NULLIF(LTRIM(RTRIM(CAST(p.[SmsContactNumber] AS nvarchar(max)))), N''),
                directory.[MobileNumber]
            ) LIKE {0} ESCAPE '~')
        """;

    // Field15 is the access number in both legacy directories. Prefer STAFF if an
    // access number happens to occur in both databases, and ignore blank mobiles.
    private const string MobileNumberLookup = """
        OUTER APPLY (
            SELECT TOP (1) NULLIF(LTRIM(RTRIM([Staff-Student-Union].[MobileNumber])), N'') AS [MobileNumber]
            FROM (
                SELECT CAST([Field15] AS nvarchar(max)) AS [Field15],
                       CAST([Field13] AS nvarchar(max)) AS [MobileNumber],
                       1 AS [SourcePriority]
                FROM [STAFF].[dbo].[MyDataTable]
                UNION ALL
                SELECT CAST([Field15] AS nvarchar(max)) AS [Field15],
                       CAST([Field10] AS nvarchar(max)) AS [MobileNumber],
                       2 AS [SourcePriority]
                FROM [STUDENT].[dbo].[MyDataTable]
            ) AS [Staff-Student-Union]
            WHERE [Staff-Student-Union].[Field15] = CAST(p.[AccessNumber] AS nvarchar(max))
              AND NULLIF(LTRIM(RTRIM([Staff-Student-Union].[MobileNumber])), N'') IS NOT NULL
            ORDER BY [Staff-Student-Union].[SourcePriority]
        ) directory
        """;
}
