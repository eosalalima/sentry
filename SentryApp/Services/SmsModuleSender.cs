using System.IO.Ports;
using System.Text;

namespace SentryApp.Services;

public sealed class SmsModuleSender
{
    private readonly IConfiguration _configuration;

    public SmsModuleSender(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public SmsSendResult TrySend(string mobileNumber, string message)
    {
        var settings = _configuration.GetSection("SmsModule").Get<SmsModuleSettings>() ?? new SmsModuleSettings();
        var portName = BuildPortName(settings.ComPort);
        if (string.IsNullOrWhiteSpace(portName))
        {
            return new SmsSendResult(false, "SMS module COM port is not configured.");
        }

        if (string.IsNullOrWhiteSpace(mobileNumber))
        {
            return new SmsSendResult(false, "SMS recipient mobile number is missing.");
        }

        return SendSms(portName, mobileNumber, message, settings);
    }

    private static string BuildPortName(int? portNumber)
    {
        if (portNumber is null || portNumber <= 0)
        {
            return string.Empty;
        }

        return $"COM{portNumber}";
    }

    private static SmsSendResult SendSms(string portName, string mobileNumber, string message, SmsModuleSettings settings)
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

        using var port = new SerialPort(portName, settings.BaudRate, parity, settings.DataBits, stopBits)
        {
            Handshake = handshake,
            ReadTimeout = settings.ReadTimeout,
            WriteTimeout = settings.WriteTimeout,
            NewLine = string.IsNullOrWhiteSpace(settings.NewLine) ? "\r\n" : settings.NewLine
        };

        try
        {
            port.Open();
            SendCommand(port, "AT");
            SendCommand(port, "AT+CMGF=1");
            SendCommand(port, "AT+CMEE=1");

            port.WriteLine($"AT+CMGS=\"{mobileNumber}\"");
            var prompt = ReadUntilPrompt(port);
            if (prompt.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                return new SmsSendResult(false, prompt);
            }

            port.Write(message + char.ConvertFromUtf32(26));
            var response = ReadResponse(port);
            var success = response.Contains("OK", StringComparison.OrdinalIgnoreCase);
            return new SmsSendResult(success, response);
        }
        catch (Exception ex)
        {
            return new SmsSendResult(false, $"SMS send failed: {ex.Message}");
        }
    }

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

    private static bool TryParseEnum<T>(string? value, T fallback, out T result) where T : struct
    {
        if (!string.IsNullOrWhiteSpace(value) && Enum.TryParse(value, true, out result))
        {
            return true;
        }

        result = fallback;
        return false;
    }
}

public sealed class SmsModuleSettings
{
    public int? ComPort { get; set; }
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public string Parity { get; set; } = "None";
    public string StopBits { get; set; } = "One";
    public string Handshake { get; set; } = "None";
    public int ReadTimeout { get; set; } = 2000;
    public int WriteTimeout { get; set; } = 2000;
    public string NewLine { get; set; } = "\r\n";
}

public sealed record SmsSendResult(bool Success, string Response);
