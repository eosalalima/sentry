using System.IO.Ports;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace SentryApp.Services;

public sealed class SmsModuleSender
{
    public const string DefaultLogFileName = "SmsSendinglog.txt";
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<SmsModuleSender> _logger;
    private readonly object _logLock = new();

    public SmsModuleSender(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<SmsModuleSender> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    public SmsSendResult TrySend(string mobileNumber, string message)
    {
        var settings = NormalizeSettings(_configuration.GetSection("SmsModule").Get<SmsModuleSettings>() ?? new SmsModuleSettings());
        return TrySend(mobileNumber, message, settings);
    }

    public SmsSendResult TrySend(string mobileNumber, string message, SmsModuleSettings settings)
    {
        settings = NormalizeSettings(settings);
        if (!settings.Enabled)
        {
            return new SmsSendResult(false, "SMS sending is disabled.");
        }

        var deviceSettings = BuildDeviceSettings(settings);
        var portName = deviceSettings.PortName;
        if (string.IsNullOrWhiteSpace(portName))
        {
            var result = new SmsSendResult(false, "SMS module COM port is not configured.");
            LogSmsStatus(mobileNumber, message, result, settings);
            return result;
        }

        if (string.IsNullOrWhiteSpace(mobileNumber))
        {
            var result = new SmsSendResult(false, "SMS recipient mobile number is missing.");
            LogSmsStatus(mobileNumber, message, result, settings);
            return result;
        }

        var sendResult = SendSms(deviceSettings, mobileNumber, message);
        LogSmsStatus(mobileNumber, message, sendResult, settings);
        return sendResult;
    }

    public SmsSendResult CheckModule(SmsModuleSettings settings)
    {
        settings = NormalizeSettings(settings);
        if (!settings.Enabled)
            return new SmsSendResult(false, "SMS sending is disabled.");

        var deviceSettings = BuildDeviceSettings(settings);
        if (string.IsNullOrWhiteSpace(deviceSettings.PortName))
            return new SmsSendResult(false, "SMS module COM port is not configured.");

        using var port = CreateSerialPort(deviceSettings);
        try
        {
            port.Open();
            foreach (var command in new[] { "AT", "AT+CMGF=1", "AT+CMEE=1" })
            {
                var response = SendCommand(port, command);
                if (!response.Contains("OK", StringComparison.OrdinalIgnoreCase))
                    return new SmsSendResult(false, DescribeFailure(response));
            }

            return new SmsSendResult(true, "SMS Module is ok, ready to send message!");
        }
        catch (Exception ex)
        {
            return new SmsSendResult(false, $"SMS module check failed: {ex.Message}");
        }
    }

    private static string BuildPortName(int? portNumber)
    {
        if (portNumber is null || portNumber <= 0)
        {
            return string.Empty;
        }

        return $"COM{portNumber}";
    }

    private static SmsSendResult SendSms(SmsDeviceSettings deviceSettings, string mobileNumber, string message)
    {
        using var port = CreateSerialPort(deviceSettings);
        try
        {
            port.Open();
            var response = SendCommand(port, "AT");
            if (IsErrorResponse(response))
            {
                return new SmsSendResult(false, DescribeFailure(response));
            }

            response = SendCommand(port, "AT+CMGF=1");
            if (IsErrorResponse(response))
            {
                return new SmsSendResult(false, DescribeFailure(response));
            }

            response = SendCommand(port, "AT+CMEE=1");
            if (IsErrorResponse(response))
            {
                return new SmsSendResult(false, DescribeFailure(response));
            }

            port.WriteLine($"AT+CMGS=\"{mobileNumber}\"");
            var prompt = ReadUntilPrompt(port);
            if (IsErrorResponse(prompt))
            {
                return new SmsSendResult(false, DescribeFailure(prompt));
            }

            port.Write(message + char.ConvertFromUtf32(26));
            response = ReadResponse(port);
            var success = response.Contains("OK", StringComparison.OrdinalIgnoreCase);
            if (success)
            {
                return new SmsSendResult(true, response);
            }

            return new SmsSendResult(false, DescribeFailure(response));
        }
        catch (Exception ex)
        {
            return new SmsSendResult(false, $"SMS send failed: {ex.Message}");
        }
    }

    private static SerialPort CreateSerialPort(SmsDeviceSettings deviceSettings) =>
        new(deviceSettings.PortName, deviceSettings.BaudRate, deviceSettings.Parity, deviceSettings.DataBits, deviceSettings.StopBits)
        {
            Handshake = deviceSettings.Handshake,
            ReadTimeout = deviceSettings.ReadTimeout,
            WriteTimeout = deviceSettings.WriteTimeout,
            NewLine = deviceSettings.NewLine
        };

    private static string SendCommand(SerialPort port, string command)
    {
        port.WriteLine(command);
        return ReadResponse(port);
    }

    private static string ReadResponse(SerialPort port)
    {
        var buffer = new StringBuilder();
        var stopAt = DateTime.UtcNow.AddMilliseconds(port.ReadTimeout);

        while (DateTime.UtcNow < stopAt)
        {
            try
            {
                var chunk = port.ReadExisting();
                if (!string.IsNullOrEmpty(chunk))
                {
                    buffer.Append(chunk);
                    var current = buffer.ToString();
                    if (current.Contains("OK", StringComparison.OrdinalIgnoreCase) ||
                        current.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }
            catch (TimeoutException)
            {
                break;
            }

            Thread.Sleep(100);
        }

        return buffer.Length == 0 ? "No response received." : buffer.ToString();
    }

    private static string ReadUntilPrompt(SerialPort port)
    {
        var buffer = new StringBuilder();
        var stopAt = DateTime.UtcNow.AddMilliseconds(port.ReadTimeout);

        while (DateTime.UtcNow < stopAt)
        {
            try
            {
                var chunk = port.ReadExisting();
                if (!string.IsNullOrEmpty(chunk))
                {
                    buffer.Append(chunk);
                    var current = buffer.ToString();
                    if (current.Contains(">", StringComparison.OrdinalIgnoreCase) ||
                        current.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }
            catch (TimeoutException)
            {
                break;
            }

            Thread.Sleep(100);
        }

        return buffer.Length == 0 ? "No response received." : buffer.ToString();
    }

    private static SmsModuleSettings NormalizeSettings(SmsModuleSettings settings)
    {
        settings.BaudRate = Math.Max(1, settings.BaudRate);
        settings.DataBits = Math.Max(5, settings.DataBits);
        settings.ReadTimeout = Math.Max(1, settings.ReadTimeout);
        settings.WriteTimeout = Math.Max(1, settings.WriteTimeout);
        settings.NewLine = string.IsNullOrWhiteSpace(settings.NewLine) ? "\r\n" : settings.NewLine;
        settings.Parity = string.IsNullOrWhiteSpace(settings.Parity) ? Parity.None.ToString() : settings.Parity;
        settings.StopBits = string.IsNullOrWhiteSpace(settings.StopBits) ? StopBits.One.ToString() : settings.StopBits;
        settings.Handshake = string.IsNullOrWhiteSpace(settings.Handshake) ? Handshake.None.ToString() : settings.Handshake;
        return settings;
    }

    private static SmsDeviceSettings BuildDeviceSettings(SmsModuleSettings settings)
    {
        if (!TryParseEnum(settings.Parity, Parity.None, out var parity))
        {
            parity = Parity.None;
        }

        if (!TryParseEnum(settings.StopBits, StopBits.One, out var stopBits))
        {
            stopBits = StopBits.One;
        }

        if (!TryParseEnum(settings.Handshake, Handshake.None, out var handshake))
        {
            handshake = Handshake.None;
        }

        return new SmsDeviceSettings(
            BuildPortName(settings.ComPort),
            settings.BaudRate,
            settings.DataBits,
            parity,
            stopBits,
            handshake,
            settings.ReadTimeout,
            settings.WriteTimeout,
            settings.NewLine);
    }

    private static bool IsErrorResponse(string response) =>
        response.Contains("ERROR", StringComparison.OrdinalIgnoreCase);

    private static string DescribeFailure(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return "SMS send failed: Empty response received.";
        }

        var trimmed = response.Trim();
        foreach (var line in trimmed.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains("CME ERROR", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("CMS ERROR", StringComparison.OrdinalIgnoreCase))
            {
                return $"SMS send failed: {line.Trim()}";
            }
        }

        if (trimmed.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            return $"SMS send failed: {trimmed}";
        }

        return $"SMS send failed: {trimmed}";
    }

    private static bool TryParseEnum<T>(string? value, T fallback, out T result) where T : struct
    {
        if (!string.IsNullOrWhiteSpace(value) && Enum.TryParse(value, true, out result))
        {
            return true;
        }

        result = fallback;
        return false;
    }

    private void LogSmsStatus(string mobileNumber, string message, SmsSendResult result, SmsModuleSettings settings)
    {
        if (!settings.Enabled || !settings.LoggingEnabled)
            return;

        try
        {
            var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var recipient = string.IsNullOrWhiteSpace(mobileNumber) ? "N/A" : mobileNumber.Trim();
            var messageBody = string.IsNullOrWhiteSpace(message)
                ? "N/A"
                : message.Replace("\r", " ").Replace("\n", " ").Trim();
            var response = string.IsNullOrWhiteSpace(result.Response)
                ? "No response returned."
                : result.Response.Replace("\r", " ").Replace("\n", " ").Trim();
            var outcomeDetails = result.Success
                ? $"Response: {response}"
                : $"Failure reason: {response}";
            var line = $"{timestamp} | To: {recipient} | Message: {messageBody} | Success: {result.Success} | {outcomeDetails}";
            var fileName = Path.GetFileName(settings.LogFileName);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = DefaultLogFileName;
            var logPath = Path.Combine(_environment.ContentRootPath, fileName);

            lock (_logLock)
            {
                File.AppendAllText(logPath, line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write SMS sending log entry.");
        }
    }
}

public sealed class SmsModuleSettings
{
    public bool Enabled { get; set; } = true;
    public int? ComPort { get; set; }
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public string Parity { get; set; } = "None";
    public string StopBits { get; set; } = "One";
    public string Handshake { get; set; } = "None";
    public int ReadTimeout { get; set; } = 2000;
    public int WriteTimeout { get; set; } = 2000;
    public string NewLine { get; set; } = "\r\n";
    public bool LoggingEnabled { get; set; }
    public string LogFileName { get; set; } = SmsModuleSender.DefaultLogFileName;
}

public sealed record SmsDeviceSettings(
    string PortName,
    int BaudRate,
    int DataBits,
    Parity Parity,
    StopBits StopBits,
    Handshake Handshake,
    int ReadTimeout,
    int WriteTimeout,
    string NewLine);

public sealed record SmsSendResult(bool Success, string Response);
