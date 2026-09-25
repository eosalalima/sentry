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
