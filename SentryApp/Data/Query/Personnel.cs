using Microsoft.EntityFrameworkCore;

namespace SentryApp.Data.Query;

[Keyless]
public sealed class Personnel
{
    public string AccessNumber { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
}
