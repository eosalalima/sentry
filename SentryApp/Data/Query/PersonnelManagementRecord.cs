namespace SentryApp.Data.Query;

public sealed class PersonnelManagementRecord
{
    public string IdNumber { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string? MiddleInitial { get; set; }
    public string? Classification { get; set; }
    public string? MobileNumber { get; set; }
}

public sealed record PersonnelPage(
    IReadOnlyList<PersonnelManagementRecord> Records,
    int TotalCount);

public enum PersonnelType
{
    Student,
    Staff
}
