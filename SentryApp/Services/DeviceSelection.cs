namespace SentryApp.Services;

public static class DeviceSelection
{
    // Preserves the existing settings value for monitoring every device.
    public const string AllDevicesValue = "1";

    public static bool Matches(string? selectedDeviceSerial, string? deviceSerialNumber)
    {
        if (string.IsNullOrWhiteSpace(selectedDeviceSerial)
            || string.Equals(selectedDeviceSerial.Trim(), AllDevicesValue, StringComparison.Ordinal))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(deviceSerialNumber)
            && string.Equals(
                selectedDeviceSerial.Trim(),
                deviceSerialNumber.Trim(),
                StringComparison.OrdinalIgnoreCase);
    }
}
