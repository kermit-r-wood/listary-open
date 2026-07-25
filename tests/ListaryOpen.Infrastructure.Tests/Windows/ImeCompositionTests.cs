using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Tests.Windows;

public sealed class ImeCompositionTests
{
    [Fact]
    public void IsComposingDoesNotThrowWithoutActiveIme()
    {
        // In automated runners there is typically no composition string; the call must be safe.
        var composing = ImeComposition.IsComposing();
        Assert.False(composing);
    }
}
