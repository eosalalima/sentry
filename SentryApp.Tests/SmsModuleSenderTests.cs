using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using SentryApp.Services;

namespace SentryApp.Tests;

public sealed class SmsModuleSenderTests
{
    [Fact]
    public void TrySend_WhenSmsSendingIsDisabled_DoesNotUseSmsModule()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SmsModule:Enabled"] = "false",
                ["SmsModule:ComPort"] = "0"
            })
            .Build();
        var sender = new SmsModuleSender(configuration, new TestWebHostEnvironment(), NullLogger<SmsModuleSender>.Instance);

        var result = sender.TrySend("09171234567", "Test message");

        Assert.False(result.Success);
        Assert.Equal("SMS sending is disabled.", result.Response);
    }

    [Fact]
    public void CheckModule_WhenSmsSendingIsDisabled_ReturnsTheConfigurationError()
    {
        var configuration = new ConfigurationBuilder().Build();
        var sender = new SmsModuleSender(configuration, new TestWebHostEnvironment(), NullLogger<SmsModuleSender>.Instance);

        var result = sender.CheckModule(new SmsModuleSettings { Enabled = false, ComPort = 1 });

        Assert.False(result.Success);
        Assert.Equal("SMS sending is disabled.", result.Response);
    }

    [Fact]
    public void CheckModule_WhenConfigurationFails_CompletesEveryProgressItem()
    {
        var configuration = new ConfigurationBuilder().Build();
        var sender = new SmsModuleSender(configuration, new TestWebHostEnvironment(), NullLogger<SmsModuleSender>.Instance);
        var updates = new List<SmsModuleCheckUpdate>();

        var result = sender.CheckModule(
            new SmsModuleSettings { Enabled = true, ComPort = null },
            updates.Add);

        Assert.False(result.Success);
        Assert.Equal(SmsModuleCheck.All, updates.Select(update => update.Check));
        Assert.All(updates, update => Assert.False(update.Success));
        Assert.Contains("not configured", updates[0].Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrySend_WhenLoggingIsEnabled_CreatesConfiguredLogForValidationFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder().Build();
            var environment = new TestWebHostEnvironment { ContentRootPath = root };
            var sender = new SmsModuleSender(configuration, environment, NullLogger<SmsModuleSender>.Instance);
            var settings = new SmsModuleSettings
            {
                Enabled = true,
                LoggingEnabled = true,
                LogFileName = "custom-sms.log",
                ComPort = null
            };

            var result = sender.TrySend("09171234567", "Test message", settings);

            Assert.False(result.Success);
            var log = File.ReadAllText(Path.Combine(root, "custom-sms.log"));
            Assert.Contains("09171234567", log);
            Assert.Contains("Failure reason: SMS module COM port is not configured.", log);
            Assert.Contains("AT commands: N/A", log);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("\r\n+CREG: 0,1\r\n\r\nOK\r\n", 1)]
    [InlineData("+CREG: 0,5\nOK\n", 5)]
    [InlineData("+CREG: 2\r\nOK\r\n", 2)]
    public void TryParseNetworkRegistration_WhenResponseIsValid_ReturnsStatus(string response, int expectedStatus)
    {
        var parsed = SmsModuleSender.TryParseNetworkRegistration(response, out var status);

        Assert.True(parsed);
        Assert.Equal(expectedStatus, status);
    }

    [Theory]
    [InlineData("ERROR")]
    [InlineData("OK")]
    [InlineData("+CREG: invalid")]
    public void TryParseNetworkRegistration_WhenResponseIsInvalid_ReturnsFalse(string response)
    {
        Assert.False(SmsModuleSender.TryParseNetworkRegistration(response, out _));
    }

    [Theory]
    [InlineData("\r\n+CSQ: 20,99\r\n\r\nOK\r\n", 20)]
    [InlineData("+CSQ: 0,0\nOK\n", 0)]
    [InlineData("+CSQ: 99,99\nOK\n", 99)]
    public void TryParseSignalQuality_WhenResponseIsValid_ReturnsRssi(string response, int expectedRssi)
    {
        var parsed = SmsModuleSender.TryParseSignalQuality(response, out var rssi);

        Assert.True(parsed);
        Assert.Equal(expectedRssi, rssi);
    }

    [Theory]
    [InlineData("ERROR")]
    [InlineData("+CSQ: 32,0")]
    [InlineData("+CSQ: invalid")]
    public void TryParseSignalQuality_WhenResponseIsInvalid_ReturnsFalse(string response)
    {
        Assert.False(SmsModuleSender.TryParseSignalQuality(response, out _));
    }

    [Theory]
    [InlineData("\r\n+CMT: \"8080\",\"\",\"26/09/25,12:00:00+00\"\r\nYour balance is 100 MB.\r\n", true)]
    [InlineData("+CMT: \"8080\"\r\nDATA BAL response", true)]
    [InlineData("+CMT: \"8080\"\r\n", false)]
    [InlineData("OK\r\n", false)]
    public void ContainsCompleteIncomingMessage_DetectsMessageBody(string response, bool expected)
    {
        Assert.Equal(expected, SmsModuleSender.ContainsCompleteIncomingMessage(response));
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "SentryApp.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
