namespace SentryApp.Services;

public sealed class TurnstileLogEntry
{
    public Guid TimeLogId { get; init; }
    public DateTimeOffset TimeLogStamp { get; init; }

    public string? LogType { get; init; }   // "IN" / "OUT"

    public string PhotoUrl { get; init; } = "/img/avatar-placeholder.svg";
    public string PersonnelName { get; init; } = "UNKNOWN";
    public string? AccessNumber { get; init; }

    public string? DeviceSerialNumber { get; init; }
    public string? DeviceName { get; init; }
    public string? VerifyMode { get; init; }
    public string? Event { get; init; }
    public string? EventAddress { get; init; }
    public string SmsStatusMessage { get; init; } = "SMS status pending.";
}

public sealed class TurnstileQueueItem
{
    public TurnstileLogEntry Entry { get; init; } = new();
    public DateTimeOffset EnqueuedAt { get; init; } = DateTimeOffset.UtcNow;
}
