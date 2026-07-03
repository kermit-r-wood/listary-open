using ListaryOpen.Infrastructure.Dialog;

namespace ListaryOpen.Infrastructure.Tests.Dialog;

public sealed class WindowsDialogAutomationTests
{
    [Theory]
    [InlineData("#32770")]
    [InlineData("MozillaDialogClass")]
    public void IsSupportedFileDialogClassAcceptsStandardAndFirefoxDialogClasses(string className)
    {
        Assert.True(WindowsDialogAutomation.IsSupportedFileDialogClass(className));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Chrome_WidgetWin_1")]
    [InlineData("MozillaWindowClass")]
    public void IsSupportedFileDialogClassRejectsBrowserMainWindowClasses(string className)
    {
        Assert.False(WindowsDialogAutomation.IsSupportedFileDialogClass(className));
    }

    [Fact]
    public void ResolveActiveDialogHandleUsesForegroundStandardDialog()
    {
        var foregroundDialog = new IntPtr(10);

        var resolved = WindowsDialogAutomation.ResolveActiveDialogHandle(
            foregroundDialog,
            handle => handle == foregroundDialog ? "#32770" : string.Empty,
            _ => "firefox",
            () => Array.Empty<IntPtr>());

        Assert.Equal(foregroundDialog, resolved);
    }

    [Fact]
    public void ResolveActiveDialogHandleFindsBrowserOwnedDialogWhenBrowserMainWindowIsForeground()
    {
        var foregroundBrowser = new IntPtr(10);
        var browserDialog = new IntPtr(20);

        var resolved = WindowsDialogAutomation.ResolveActiveDialogHandle(
            foregroundBrowser,
            handle => handle == browserDialog ? "#32770" : "MozillaWindowClass",
            handle => handle == browserDialog ? "firefox" : "firefox",
            () => new[] { browserDialog });

        Assert.Equal(browserDialog, resolved);
    }

    [Fact]
    public void ResolveActiveDialogHandleDoesNotTargetUnrelatedBrowserDialogWhenListaryIsForeground()
    {
        var searchPanel = new IntPtr(10);
        var firefoxDialog = new IntPtr(20);
        var chromeDialog = new IntPtr(30);

        var resolved = WindowsDialogAutomation.ResolveActiveDialogHandle(
            searchPanel,
            handle => handle == searchPanel ? "HwndWrapper[ListaryOpen.App;;]" : "#32770",
            handle => handle == searchPanel
                ? "ListaryOpen.App"
                : handle == firefoxDialog
                    ? "firefox"
                    : "chrome",
            () => new[] { firefoxDialog, chromeDialog });

        Assert.Equal(IntPtr.Zero, resolved);
    }

    [Theory]
    [InlineData("firefox")]
    [InlineData("chrome")]
    [InlineData("msedge")]
    public void IsKnownBrowserProcessNameAcceptsSupportedBrowsers(string processName)
    {
        Assert.True(WindowsDialogAutomation.IsKnownBrowserProcessName(processName));
    }

    [Fact]
    public void IsFileNameControlCandidateAcceptsFirefoxUploadEditPane()
    {
        Assert.True(WindowsDialogAutomation.IsFileNameControlCandidate(
            "1148",
            string.Empty,
            "Edit",
            "ControlType.Pane"));
    }

    [Fact]
    public void IsFileNameControlCandidateRejectsShellItemNameEdits()
    {
        Assert.False(WindowsDialogAutomation.IsFileNameControlCandidate(
            "System.ItemNameDisplay",
            "Name",
            "UIProperty",
            "ControlType.Edit"));
    }

    [Fact]
    public void IsCommitButtonCandidateAcceptsFirefoxUploadOpenButtonPane()
    {
        Assert.True(WindowsDialogAutomation.IsCommitButtonCandidate(
            "1",
            "Open",
            "Button",
            "ControlType.Pane"));
    }
}
