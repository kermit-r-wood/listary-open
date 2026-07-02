using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Tests.Windows;

public sealed class HotkeyServiceTests
{
    [Fact]
    public void RaiseForTestsPublishesHotkeyName()
    {
        using var service = new HotkeyService();
        var receivedName = string.Empty;

        service.HotkeyPressed += (_, name) => receivedName = name;

        service.RaiseForTests("Search");

        Assert.Equal("Search", receivedName);
    }
}
