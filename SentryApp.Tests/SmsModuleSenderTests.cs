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
