using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
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

    public SmsSendResult CheckModule(
        SmsModuleSettings settings,
        Action<SmsModuleCheckUpdate>? reportProgress = null)
    {
        var incompleteChecks = new List<SmsModuleCheck>(SmsModuleCheck.All);
        SmsModuleCheck? currentCheck = SmsModuleCheck.Connection;
        void Report(SmsModuleCheck check, bool success, string? detail = null)
        {
            incompleteChecks.Remove(check);
            reportProgress?.Invoke(new SmsModuleCheckUpdate(check, success, detail));
        }

        SmsSendResult Fail(SmsModuleCheck? check, string response)
        {
            if (check is not null)
                Report(check, false, response);

            foreach (var skippedCheck in incompleteChecks.ToArray())
                Report(skippedCheck, false, "Not checked because an earlier check failed.");

            return new SmsSendResult(false, response);
        }

        settings = NormalizeSettings(settings);
        if (!settings.Enabled)
            return Fail(null, "SMS sending is disabled.");

        var deviceSettings = BuildDeviceSettings(settings);
        if (string.IsNullOrWhiteSpace(deviceSettings.PortName))
            return Fail(SmsModuleCheck.Connection, "SMS module COM port is not configured.");

        try
        {
            using var port = CreateSerialPort(deviceSettings);
            port.Open();
            Report(SmsModuleCheck.Connection, true, $"Connected to {deviceSettings.PortName}.");

            foreach (var (command, check) in new[]
            {
                ("AT", SmsModuleCheck.Modem),
                ("AT+CMGF=1", SmsModuleCheck.SmsMode),
                ("AT+CMEE=1", SmsModuleCheck.ErrorReporting)
            })
            {
                currentCheck = check;
                var response = SendCommand(port, command);
                if (!response.Contains("OK", StringComparison.OrdinalIgnoreCase))
                    return Fail(check, DescribeFailure(response));

                Report(check, true);
            }

            currentCheck = SmsModuleCheck.Network;
            var registrationResponse = SendCommand(port, "AT+CREG?");
            if (!TryParseNetworkRegistration(registrationResponse, out var registrationStatus))
            {
                return Fail(SmsModuleCheck.Network,
                    $"SMS module check failed: Could not determine network registration. {DescribeModemResponse(registrationResponse)}");
            }

            if (registrationStatus is not (1 or 5))
            {
                return Fail(SmsModuleCheck.Network,
                    $"SMS module is not registered to a network ({DescribeRegistrationStatus(registrationStatus)}).");
            }

            var network = registrationStatus == 5 ? "roaming" : "home";
            Report(SmsModuleCheck.Network, true, $"Registered to the {network} network.");

            currentCheck = SmsModuleCheck.Signal;
            var signalResponse = SendCommand(port, "AT+CSQ");
            if (!TryParseSignalQuality(signalResponse, out var signalQuality) || signalQuality == 99)
            {
                return Fail(SmsModuleCheck.Signal,
                    $"SMS module is registered to the network, but signal strength is unavailable. {DescribeModemResponse(signalResponse)}");
            }

            var signalDbm = -113 + (2 * signalQuality);
            Report(SmsModuleCheck.Signal, true, $"{signalQuality}/31 ({signalDbm} dBm)");
            return new SmsSendResult(true,
                $"SMS module is ready, registered to the {network} network, with signal strength {signalQuality}/31 ({signalDbm} dBm).");
        }
        catch (Exception ex)
        {
            return Fail(currentCheck, $"SMS module check failed: {ex.Message}");
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
        var atLog = new List<AtCommandLogEntry>();
        try
        {
            port.Open();
            var response = SendCommand(port, "AT", atLog);
            if (IsErrorResponse(response))
            {
                return CreateResult(false, DescribeFailure(response), atLog);
            }

            response = SendCommand(port, "AT+CMGF=1", atLog);
            if (IsErrorResponse(response))
            {
                return CreateResult(false, DescribeFailure(response), atLog);
            }

            response = SendCommand(port, "AT+CMEE=1", atLog);
            if (IsErrorResponse(response))
            {
                return CreateResult(false, DescribeFailure(response), atLog);
            }

            // Ask the modem to forward newly received messages directly over the
            // serial connection. This allows request/reply services (for example,
            // sending DATA BAL to a short code) to return their reply while the
            // test transaction is still open.
            response = SendCommand(port, "AT+CNMI=2,2,0,0,0", atLog);
            if (IsErrorResponse(response))
            {
                atLog.Add(new AtCommandLogEntry("Info",
                    "The modem does not support direct incoming message delivery."));
            }

            var sendMessageCommand = $"AT+CMGS=\"{mobileNumber}\"";
            atLog.Add(new AtCommandLogEntry("Sent", sendMessageCommand));
            port.WriteLine(sendMessageCommand);
            var prompt = ReadUntilPrompt(port);
            atLog.Add(new AtCommandLogEntry("Received", prompt));
            if (IsErrorResponse(prompt))
            {
                return CreateResult(false, DescribeFailure(prompt), atLog);
            }

            port.Write(message + char.ConvertFromUtf32(26));
            response = ReadResponse(port);
            atLog.Add(new AtCommandLogEntry("Received", response));
            var success = response.Contains("OK", StringComparison.OrdinalIgnoreCase);
            if (success)
            {
                var incomingMessage = ReadIncomingMessage(port, deviceSettings.ReplyTimeout);
                atLog.Add(new AtCommandLogEntry("Incoming message", incomingMessage));
                return CreateResult(true, response, atLog);
            }

            return CreateResult(false, DescribeFailure(response), atLog);
        }
        catch (Exception ex)
        {
            return CreateResult(false, $"SMS send failed: {ex.Message}", atLog);
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

    private static string SendCommand(
        SerialPort port,
        string command,
        ICollection<AtCommandLogEntry>? atLog = null)
    {
        atLog?.Add(new AtCommandLogEntry("Sent", command));
        port.WriteLine(command);
        var response = ReadResponse(port);
        atLog?.Add(new AtCommandLogEntry("Received", response));
        return response;
    }

    private static SmsSendResult CreateResult(
        bool success,
        string response,
        IReadOnlyList<AtCommandLogEntry> atLog) =>
        new(success, response) { AtCommandLog = atLog };

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

    private static string ReadIncomingMessage(SerialPort port, int replyTimeout)
    {
        var buffer = new StringBuilder();
        var stopAt = DateTime.UtcNow.AddMilliseconds(replyTimeout);

        while (DateTime.UtcNow < stopAt)
        {
            var chunk = port.ReadExisting();
            if (!string.IsNullOrEmpty(chunk))
            {
                buffer.Append(chunk);
                if (ContainsCompleteIncomingMessage(buffer.ToString()))
                    break;
            }

            Thread.Sleep(100);
        }

        return buffer.Length == 0
            ? $"No incoming message received within {replyTimeout / 1000d:0.#} seconds."
            : buffer.ToString();
    }

    internal static bool ContainsCompleteIncomingMessage(string response)
    {
        var cmtIndex = response.IndexOf("+CMT:", StringComparison.OrdinalIgnoreCase);
        if (cmtIndex < 0)
            return false;

        var message = response[cmtIndex..];
        var lines = message.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
        return lines.Length >= 2 && !string.IsNullOrWhiteSpace(lines[1]);
    }

    private static SmsModuleSettings NormalizeSettings(SmsModuleSettings settings)
    {
        settings.BaudRate = Math.Max(1, settings.BaudRate);
        settings.DataBits = Math.Max(5, settings.DataBits);
        settings.ReadTimeout = Math.Max(1, settings.ReadTimeout);
        settings.WriteTimeout = Math.Max(1, settings.WriteTimeout);
        settings.ReplyTimeout = Math.Max(1, settings.ReplyTimeout);
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
            settings.ReplyTimeout,
            settings.NewLine);
    }

    private static bool IsErrorResponse(string response) =>
        response.Contains("ERROR", StringComparison.OrdinalIgnoreCase);

    internal static bool TryParseNetworkRegistration(string response, out int status)
    {
        var match = Regex.Match(
            response ?? string.Empty,
            @"\+CREG:\s*(?:(?:[0-2])\s*,\s*)?([0-5])\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return int.TryParse(match.Success ? match.Groups[1].Value : null, out status);
    }

    internal static bool TryParseSignalQuality(string response, out int signalQuality)
    {
        var match = Regex.Match(
            response ?? string.Empty,
            @"\+CSQ:\s*(\d{1,2})\s*,\s*\d{1,2}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!int.TryParse(match.Success ? match.Groups[1].Value : null, out signalQuality))
            return false;

        return signalQuality is >= 0 and <= 31 or 99;
    }

    private static string DescribeRegistrationStatus(int status) => status switch
    {
        0 => "not registered and not searching",
        2 => "searching for a network",
        3 => "registration denied",
        4 => "registration status unknown",
        _ => "not registered"
    };

    private static string DescribeModemResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response) || response == "No response received.")
            return "No response received.";

        return $"Modem response: {response.Trim()}";
    }

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
            var atCommands = result.AtCommandLog.Count == 0
                ? "N/A"
                : string.Join(" | ", result.AtCommandLog.Select(entry =>
                    $"{entry.Direction}: {SanitizeLogValue(entry.Value)}"));
            var line = $"{timestamp} | To: {recipient} | Message: {messageBody} | Success: {result.Success} | {outcomeDetails} | AT commands: {atCommands}";
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

    private static string SanitizeLogValue(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "N/A"
            : value.Replace("\r", " ").Replace("\n", " ").Trim();
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
    public int ReplyTimeout { get; set; } = 30000;
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
    int ReplyTimeout,
    string NewLine);

public sealed record AtCommandLogEntry(string Direction, string Value);

public sealed record SmsModuleCheck(string Name)
{
    public static readonly SmsModuleCheck Connection = new("Connection");
    public static readonly SmsModuleCheck Modem = new("Modem response");
    public static readonly SmsModuleCheck SmsMode = new("SMS mode");
    public static readonly SmsModuleCheck ErrorReporting = new("Error reporting");
    public static readonly SmsModuleCheck Network = new("Network registration");
    public static readonly SmsModuleCheck Signal = new("Signal");

    public static IReadOnlyList<SmsModuleCheck> All { get; } =
        new[] { Connection, Modem, SmsMode, ErrorReporting, Network, Signal };
}

public sealed record SmsModuleCheckUpdate(SmsModuleCheck Check, bool Success, string? Detail = null);

public sealed record SmsSendResult(bool Success, string Response)
{
    public IReadOnlyList<AtCommandLogEntry> AtCommandLog { get; init; } = Array.Empty<AtCommandLogEntry>();
}
