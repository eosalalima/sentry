namespace SentryApp.Services;

public static class SmsRecipientResolver
{
    public static string? Resolve(bool isLiveMode, string? databaseMobileNumber, string? demoRecipientNumber)
    {
        var recipient = isLiveMode ? databaseMobileNumber : demoRecipientNumber;
        return string.IsNullOrWhiteSpace(recipient) ? null : recipient.Trim();
    }
}
