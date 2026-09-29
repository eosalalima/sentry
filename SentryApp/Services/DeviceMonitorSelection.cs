namespace SentryApp.Services;

/// <summary>
/// Holds the device selected by one monitoring dashboard session.
/// </summary>
public sealed class DeviceMonitorSelection
{
    private string _serialNumber = DeviceSelection.AllDevicesValue;

    public string SerialNumber => _serialNumber;

    public event Action? Changed;

    public void Set(string? serialNumber)
    {
        var normalized = string.IsNullOrWhiteSpace(serialNumber)
            ? DeviceSelection.AllDevicesValue
            : serialNumber.Trim();

        if (string.Equals(_serialNumber, normalized, StringComparison.OrdinalIgnoreCase))
            return;

        _serialNumber = normalized;
        Changed?.Invoke();
    }
}
