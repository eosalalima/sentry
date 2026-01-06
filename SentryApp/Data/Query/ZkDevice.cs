using Microsoft.EntityFrameworkCore;

namespace SentryApp.Data.Query;

[Keyless]
public sealed class ZkDevice
{
    public string SerialNumber { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
