namespace SentryApp.Data.Query;

public sealed class PersonnelManagementRecord
{
    public string PersonnelNo { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string? PhotoId { get; set; }
    public string? SmsContactNumber { get; set; }
}

public sealed record PersonnelPage(
    IReadOnlyList<PersonnelManagementRecord> Records,
    int TotalCount);
