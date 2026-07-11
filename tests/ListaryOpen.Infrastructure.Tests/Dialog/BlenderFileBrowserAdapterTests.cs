using ListaryOpen.Infrastructure.Dialog;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Tests.Dialog;

public sealed class BlenderFileBrowserAdapterTests
{
    [Fact]
    public void CanHandleActiveWindowRecognizesBlenderFileBrowser()
    {
        var adapter = new BlenderFileBrowserAdapter(
            () => new DialogWindowSnapshot(
                new IntPtr(100),
                "blender",
                "GHOST_WindowClass",
                "Blender File View"),
            _ => true,
            _ => true,
            _ => { });

        Assert.True(adapter.CanHandleActiveWindow());
    }

    [Theory]
    [InlineData("notepad", "GHOST_WindowClass", "Blender File View")]
    [InlineData("blender", "#32770", "Blender File View")]
    [InlineData("blender", "GHOST_WindowClass", "Blender")]
    public void CanHandleActiveWindowRejectsOtherWindows(string processName, string className, string title)
    {
        var adapter = new BlenderFileBrowserAdapter(
            () => new DialogWindowSnapshot(new IntPtr(100), processName, className, title),
            _ => true,
            _ => true,
            _ => { });

        Assert.False(adapter.CanHandleActiveWindow());
    }

    [Fact]
    public async Task SetFolderAsyncForegroundsBlenderAndSendsFolderNavigation()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-blender-adapter-");
        var foregroundedWindows = new List<IntPtr>();
        IReadOnlyList<NativeMethods.Input>? sentInputs = null;
        var adapter = new BlenderFileBrowserAdapter(
            () => new DialogWindowSnapshot(
                new IntPtr(200),
                "blender.exe",
                "GHOST_WindowClass",
                "Blender File View"),
            handle =>
            {
                foregroundedWindows.Add(handle);
                return true;
            },
            inputs =>
            {
                sentInputs = inputs;
                return true;
            },
            _ => { });

        try
        {
            var result = await adapter.SetFolderAsync(folder.FullName, CancellationToken.None);

            Assert.True(result);
            Assert.Equal(new[] { new IntPtr(200) }, foregroundedWindows);
            Assert.NotNull(sentInputs);
            Assert.Equal(
                new[] { NativeMethods.VkControl, NativeMethods.VkL, NativeMethods.VkL, NativeMethods.VkControl },
                sentInputs!.Take(4).Select(input => input.Union.Keyboard.VirtualKey));
            Assert.Contains(sentInputs, input => input.Union.Keyboard.ScanCode == folder.FullName[^1]);
            Assert.Equal(NativeMethods.VkReturn, sentInputs[^2].Union.Keyboard.VirtualKey);
            Assert.Equal(NativeMethods.VkReturn, sentInputs[^1].Union.Keyboard.VirtualKey);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
