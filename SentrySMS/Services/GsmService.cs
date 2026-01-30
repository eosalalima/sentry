using System.IO.Ports;
using System.Text;
using Microsoft.Extensions.Options;
using SentrySMS.Models;

namespace SentrySMS.Services;

public record GsmResult(bool Success, string Response);

public class GsmService
{
    private readonly IOptionsMonitor<GsmSettings> _settingsMonitor;

    public GsmService(IOptionsMonitor<GsmSettings> settingsMonitor)
    {
        _settingsMonitor = settingsMonitor;
    }

    public Task<GsmResult> DetectDeviceAsync(GsmSettings settings, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(settings.SerialPort))
            {
                return new GsmResult(false, "Serial port is required.");
            }

            if (!SerialPort.GetPortNames().Contains(settings.SerialPort, StringComparer.OrdinalIgnoreCase))
            {
                return new GsmResult(false, $"No device detected on {settings.SerialPort}.");
            }

            using var port = BuildPort(settings);
            try
            {
                port.Open();
                var response = SendCommand(port, "AT", cancellationToken);
                return new GsmResult(response.Contains("OK", StringComparison.OrdinalIgnoreCase), response);
            }
            catch (Exception ex)
            {
                return new GsmResult(false, $"Unable to open port: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<GsmResult> SendSmsAsync(SmsMessage message, CancellationToken cancellationToken = default)
    {
        var settings = _settingsMonitor.CurrentValue;
        return Task.Run(() =>
        {
            using var port = BuildPort(settings);
            try
            {
                port.Open();
                var handshake = new StringBuilder();
                handshake.AppendLine(SendCommand(port, "AT", cancellationToken));
                handshake.AppendLine(SendCommand(port, "AT+CMGF=1", cancellationToken));

                port.WriteLine($"AT+CMGS=\"{message.MobileNumber}\"");
                Thread.Sleep(500);
                port.Write(message.TextMessage + char.ConvertFromUtf32(26));

                var response = ReadResponse(port, cancellationToken);
                var success = response.Contains("OK", StringComparison.OrdinalIgnoreCase);
                return new GsmResult(success, response);
            }
            catch (Exception ex)
            {
                return new GsmResult(false, $"SMS send failed: {ex.Message}");
            }
        }, cancellationToken);
    }

    private static SerialPort BuildPort(GsmSettings settings)
    {
        return new SerialPort(settings.SerialPort, settings.BaudRate, settings.Parity, settings.DataBits, settings.StopBits)
        {
            Handshake = settings.Handshake,
            ReadTimeout = settings.ReadTimeout,
            WriteTimeout = settings.WriteTimeout,
            NewLine = settings.NewLine
        };
    }

    private static string SendCommand(SerialPort port, string command, CancellationToken cancellationToken)
    {
        port.WriteLine(command);
        return ReadResponse(port, cancellationToken);
    }

    private static string ReadResponse(SerialPort port, CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();
        var stopAt = DateTime.UtcNow.AddMilliseconds(port.ReadTimeout);

        while (DateTime.UtcNow < stopAt && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var chunk = port.ReadExisting();
                if (!string.IsNullOrEmpty(chunk))
                {
                    buffer.Append(chunk);
                    if (buffer.ToString().Contains("OK", StringComparison.OrdinalIgnoreCase) ||
                        buffer.ToString().Contains("ERROR", StringComparison.OrdinalIgnoreCase))
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
}
