namespace SentryApp.Data.Query;

public sealed class TurnstileLogRow
{
    public Guid TimeLogId { get; set; }
    public DateTimeOffset TimeLogStamp { get; set; }

    public string? LogType { get; set; }

    public string? AccessNumber { get; set; }
    public string? DeviceSerialNumber { get; set; }

    public string? TimeLogVerifyMode { get; set; }

    public string? LastName { get; set; }
    public string? FirstName { get; set; }
    public string? PhotoId { get; set; }

    public string? Event { get; set; }
    public string? EventAddress { get; set; }

    public string? DeviceName { get; set; }
    public string? DeviceLogVerifyMode { get; set; }
}
