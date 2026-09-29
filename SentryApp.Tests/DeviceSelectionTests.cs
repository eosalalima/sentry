using SentryApp.Services;

namespace SentryApp.Tests;

public class DeviceSelectionTests
{
    [Theory]
    [InlineData(null, "gate-a")]
    [InlineData("", "gate-a")]
    [InlineData(DeviceSelection.AllDevicesValue, "gate-a")]
    [InlineData(" gate-a ", "GATE-A")]
    public void Matches_ReturnsTrue_ForAllDevicesOrTheSelectedDevice(string? selectedDeviceSerial, string? deviceSerialNumber)
    {
        Assert.True(DeviceSelection.Matches(selectedDeviceSerial, deviceSerialNumber));
    }

    [Theory]
    [InlineData("gate-a", "gate-b")]
    [InlineData("gate-a", null)]
    [InlineData("gate-a", "")]
    public void Matches_ReturnsFalse_WhenTheDeviceDoesNotMatch(string selectedDeviceSerial, string? deviceSerialNumber)
    {
        Assert.False(DeviceSelection.Matches(selectedDeviceSerial, deviceSerialNumber));
    }
}
