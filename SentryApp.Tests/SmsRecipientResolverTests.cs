using SentryApp.Services;

namespace SentryApp.Tests;

public sealed class SmsRecipientResolverTests
{
    [Fact]
    public void Resolve_InDemoMode_UsesConfiguredDemoRecipient()
    {
        var recipient = SmsRecipientResolver.Resolve(
            isLiveMode: false,
            databaseMobileNumber: "09170000001",
            demoRecipientNumber: " 09179999999 ");

        Assert.Equal("09179999999", recipient);
    }

    [Fact]
    public void Resolve_InLiveMode_UsesDatabaseMobileNumber()
    {
        var recipient = SmsRecipientResolver.Resolve(
            isLiveMode: true,
            databaseMobileNumber: " 09170000001 ",
            demoRecipientNumber: "09179999999");

        Assert.Equal("09170000001", recipient);
    }

    [Fact]
    public void Resolve_InLiveModeWithoutDatabaseMobileNumber_DoesNotFallBackToDemoRecipient()
    {
        var recipient = SmsRecipientResolver.Resolve(
            isLiveMode: true,
            databaseMobileNumber: null,
            demoRecipientNumber: "09179999999");

        Assert.Null(recipient);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_InDemoModeWithoutConfiguredRecipient_ReturnsNull(string? demoRecipientNumber)
    {
        var recipient = SmsRecipientResolver.Resolve(false, "09170000001", demoRecipientNumber);

        Assert.Null(recipient);
    }
}
