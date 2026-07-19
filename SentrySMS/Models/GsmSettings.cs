using System.ComponentModel.DataAnnotations;
using System.IO.Ports;

namespace SentrySMS.Models;

public class GsmSettings
{
    public const string SectionName = "GsmSettings";

    [Required]
    public string SerialPort { get; set; } = "COM3";

    [Range(1200, 115200)]
    public int BaudRate { get; set; } = 9600;

    [Range(5, 8)]
    public int DataBits { get; set; } = 8;

    public Parity Parity { get; set; } = Parity.None;

    public StopBits StopBits { get; set; } = StopBits.One;

    public Handshake Handshake { get; set; } = Handshake.None;

    [Range(500, 10000)]
    public int ReadTimeout { get; set; } = 2000;

    [Range(500, 10000)]
    public int WriteTimeout { get; set; } = 2000;

    public string NewLine { get; set; } = "\r\n";
}
