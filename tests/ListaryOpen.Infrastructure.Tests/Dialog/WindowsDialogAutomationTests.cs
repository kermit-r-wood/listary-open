using System.Runtime.InteropServices;
using ListaryOpen.Infrastructure.Dialog;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Tests.Dialog;

public sealed class WindowsDialogAutomationTests
{
    [Fact]
    public void SendInputInputStructMatchesNativeWin32Size()
    {
        var expectedSize = IntPtr.Size == 8 ? 40 : 28;

        Assert.Equal(expectedSize, Marshal.SizeOf<NativeMethods.Input>());
    }

    [Theory]
    [InlineData("Address: C:\\Users\\paulx\\Downloads", "C:\\Users\\paulx\\Downloads")]
    [InlineData("C:\\Users\\paulx\\Downloads", "C:\\Users\\paulx\\Downloads\\")]
    public void MatchesDialogAddressFolderValueAcceptsCurrentFolderAddress(string addressValue, string targetFolder)
    {
        Assert.True(WindowsDialogAutomation.MatchesDialogAddressFolderValue(
            addressValue,
            WindowsDialogAutomation.NormalizeFolderPathForTests(targetFolder)));
    }

    [Fact]
    public void MatchesDialogAddressFolderValueRejectsDifferentFolder()
    {
        Assert.False(WindowsDialogAutomation.MatchesDialogAddressFolderValue(
            "Address: C:\\Users\\paulx\\Desktop",
            WindowsDialogAutomation.NormalizeFolderPathForTests("C:\\Users\\paulx\\Downloads")));
    }

    [Fact]
    public async Task SubmitFolderNavigationWithKeyboardDoesNotFreezeRedrawBeforeConfirming()
    {
        var dialogHandle = new IntPtr(42);
        var events = new List<string>();

        var result = await WindowsDialogAutomation.SubmitFolderNavigationWithKeyboardAsync(
            dialogHandle,
            "C:\\Users\\paulx\\Downloads",
            () =>
            {
                events.Add("focus-file-name");
                return true;
            },
            folderPath =>
            {
                events.Add("send-path:" + folderPath);
                return true;
            },
            () =>
            {
                events.Add("send-enter");
                return true;
            },
            () =>
            {
                events.Add("confirm-address");
                return Task.FromResult(true);
            });

        Assert.True(result);
        Assert.Equal(
            new[]
            {
                "focus-file-name",
                "send-path:C:\\Users\\paulx\\Downloads",
                "send-enter",
                "confirm-address"
            },
            events);
    }

    [Fact]
    public void TryFocusFileNameEditWindowRejectsUnchangedForegroundWindow()
    {
        var dialogHandle = new IntPtr(100);
        var fileNameEditHandle = new IntPtr(200);
        var otherForegroundHandle = new IntPtr(300);
        var events = new List<string>();

        var result = WindowsDialogAutomation.TryFocusFileNameEditWindow(
            dialogHandle,
            fileNameEditHandle,
            handle =>
            {
                events.Add("set-foreground:" + handle);
                return false;
            },
            handle =>
            {
                events.Add("set-focus:" + handle);
                return IntPtr.Zero;
            },
            () =>
            {
                events.Add("get-foreground");
                return otherForegroundHandle;
            },
            () =>
            {
                events.Add("get-focused-window");
                return IntPtr.Zero;
            },
            _ => IntPtr.Zero,
            () => events.Add("wait"));

        Assert.False(result);
        Assert.DoesNotContain("set-focus:" + fileNameEditHandle, events);
    }

    [Fact]
    public void TryFocusFileNameEditWindowAcceptsFocusedFileNameEditWindow()
    {
        var dialogHandle = new IntPtr(100);
        var fileNameEditHandle = new IntPtr(200);
        var events = new List<string>();

        var result = WindowsDialogAutomation.TryFocusFileNameEditWindow(
            dialogHandle,
            fileNameEditHandle,
            handle =>
            {
                events.Add("set-foreground:" + handle);
                return true;
            },
            handle =>
            {
                events.Add("set-focus:" + handle);
                return IntPtr.Zero;
            },
            () =>
            {
                events.Add("get-foreground");
                return dialogHandle;
            },
            () =>
            {
                events.Add("get-focused-window");
                return fileNameEditHandle;
            },
            _ => IntPtr.Zero,
            () => events.Add("wait"));

        Assert.True(result);
        Assert.Contains("set-focus:" + fileNameEditHandle, events);
    }

    [Fact]
    public async Task SubmitFolderNavigationConfirmsAfterInvokingCommit()
    {
        var events = new List<string>();

        var result = await WindowsDialogAutomation.SubmitFolderNavigationAsync(
            () =>
            {
                events.Add("set-value");
                return true;
            },
            () =>
            {
                events.Add("invoke-open");
                return true;
            },
            () =>
            {
                events.Add("confirm-navigation");
                return Task.FromResult(true);
            });

        Assert.True(result);
        Assert.Equal(
            new[]
            {
                "set-value",
                "invoke-open",
                "confirm-navigation"
            },
            events);
    }

    [Fact]
    public async Task SubmitFolderNavigationSkipsInvokeAndConfirmWhenSetValueFails()
    {
        var events = new List<string>();

        var result = await WindowsDialogAutomation.SubmitFolderNavigationAsync(
            () =>
            {
                events.Add("set-value");
                return false;
            },
            () =>
            {
                events.Add("invoke-open");
                return true;
            },
            () =>
            {
                events.Add("confirm-navigation");
                return Task.FromResult(true);
            });

        Assert.False(result);
        Assert.Equal(
            new[]
            {
                "set-value"
            },
            events);
    }

    [Fact]
    public async Task SubmitFolderNavigationSkipsConfirmWhenInvokeFails()
    {
        var events = new List<string>();

        var result = await WindowsDialogAutomation.SubmitFolderNavigationAsync(
            () =>
            {
                events.Add("set-value");
                return true;
            },
            () =>
            {
                events.Add("invoke-open");
                return false;
            },
            () =>
            {
                events.Add("confirm-navigation");
                return Task.FromResult(true);
            });

        Assert.False(result);
        Assert.Equal(
            new[]
            {
                "set-value",
                "invoke-open"
            },
            events);
    }

    [Fact]
    public async Task SubmitFolderNavigationPropagatesSetValueCancellation()
    {
        var events = new List<string>();

        await Assert.ThrowsAsync<OperationCanceledException>(() => WindowsDialogAutomation.SubmitFolderNavigationAsync(
            () =>
            {
                events.Add("set-value");
                throw new OperationCanceledException();
            },
            () =>
            {
                events.Add("invoke-open");
                return true;
            },
            () =>
            {
                events.Add("confirm-navigation");
                return Task.FromResult(true);
            }));

        Assert.Equal(
            new[]
            {
                "set-value"
            },
            events);
    }

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
            _ => IntPtr.Zero,
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
            handle => handle == browserDialog ? foregroundBrowser : IntPtr.Zero,
            () => new[] { browserDialog });

        Assert.Equal(browserDialog, resolved);
    }

    [Fact]
    public void ResolveActiveDialogHandleIgnoresSameProcessDialogWhenBrowserDoesNotOwnIt()
    {
        var foregroundBrowser = new IntPtr(10);
        var unrelatedDialog = new IntPtr(20);

        var resolved = WindowsDialogAutomation.ResolveActiveDialogHandle(
            foregroundBrowser,
            handle => handle == unrelatedDialog ? "#32770" : "MozillaWindowClass",
            _ => "firefox",
            _ => IntPtr.Zero,
            () => new[] { unrelatedDialog });

        Assert.Equal(IntPtr.Zero, resolved);
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
            _ => IntPtr.Zero,
            () => new[] { firefoxDialog, chromeDialog });

        Assert.Equal(IntPtr.Zero, resolved);
    }

    [Fact]
    public void ResolveActiveDialogHandleFindsSingleBrowserOwnedDialogWhenListaryIsForeground()
    {
        var searchPanel = new IntPtr(10);
        var firefoxDialog = new IntPtr(20);
        var firefoxWindow = new IntPtr(30);

        var resolved = WindowsDialogAutomation.ResolveActiveDialogHandle(
            searchPanel,
            handle => handle == searchPanel
                ? "HwndWrapper[ListaryOpen.App;;]"
                : handle == firefoxWindow
                    ? "MozillaWindowClass"
                    : "#32770",
            handle => handle == searchPanel
                ? "ListaryOpen.App"
                : "firefox",
            handle => handle == firefoxDialog ? firefoxWindow : IntPtr.Zero,
            () => new[] { firefoxDialog, firefoxWindow });

        Assert.Equal(firefoxDialog, resolved);
    }

    [Fact]
    public void ResolveActiveDialogHandleIgnoresListaryForegroundWhenMultipleBrowserOwnedDialogsExist()
    {
        var searchPanel = new IntPtr(10);
        var firefoxDialog = new IntPtr(20);
        var firefoxWindow = new IntPtr(30);
        var chromeDialog = new IntPtr(40);
        var chromeWindow = new IntPtr(50);

        var resolved = WindowsDialogAutomation.ResolveActiveDialogHandle(
            searchPanel,
            handle => handle == searchPanel
                ? "HwndWrapper[ListaryOpen.App;;]"
                : handle == firefoxWindow
                    ? "MozillaWindowClass"
                    : handle == chromeWindow
                        ? "Chrome_WidgetWin_1"
                        : "#32770",
            handle => handle == searchPanel
                ? "ListaryOpen.App"
                : handle == chromeDialog || handle == chromeWindow
                    ? "chrome"
                    : "firefox",
            handle => handle == firefoxDialog
                ? firefoxWindow
                : handle == chromeDialog
                    ? chromeWindow
                    : IntPtr.Zero,
            () => new[] { firefoxDialog, firefoxWindow, chromeDialog, chromeWindow });

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
