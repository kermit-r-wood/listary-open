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

    [Fact]
    public void ClassifyFileDialogControlsRecognizesModernGeneralDialog()
    {
        var shape = WindowsDialogAutomation.ClassifyFileDialogControls(new[]
        {
            "DirectUIHWND1",
            "ToolbarWindow321",
            "Edit1"
        });

        Assert.Equal(FileDialogControlShape.ModernGeneral, shape);
    }

    [Fact]
    public void ClassifyFileDialogControlsRecognizesLegacySysListViewDialog()
    {
        var shape = WindowsDialogAutomation.ClassifyFileDialogControls(new[]
        {
            "SysListView321",
            "SHELLDLL_DefView1",
            "SysHeader321",
            "Edit1"
        });

        Assert.Equal(FileDialogControlShape.LegacySysListView, shape);
    }

    [Fact]
    public void ClassifyFileDialogControlsRecognizesBrowseFolderDialog()
    {
        var shape = WindowsDialogAutomation.ClassifyFileDialogControls(new[]
        {
            "SHBrowseForFolder ShellNameSpace Control",
            "SysTreeView321"
        });

        Assert.Equal(FileDialogControlShape.BrowseFolder, shape);
    }

    [Fact]
    public void IsAutomatableFileDialogShapeRejectsBrowseFolderUntilTreeJumpIsImplemented()
    {
        Assert.False(WindowsDialogAutomation.IsAutomatableFileDialogShape(FileDialogControlShape.BrowseFolder));
    }

    [Fact]
    public void ClassifyFileDialogControlsRejectsUnrecognizedControls()
    {
        var shape = WindowsDialogAutomation.ClassifyFileDialogControls(new[]
        {
            "Button1",
            "Static1"
        });

        Assert.Equal(FileDialogControlShape.Unsupported, shape);
    }

    [Fact]
    public async Task SubmitFolderNavigationWithKeyboardReturnsSuccessAfterWin32Commit()
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
            });

        Assert.True(result);
        Assert.Equal(
            new[]
            {
                "focus-file-name",
                "send-path:C:\\Users\\paulx\\Downloads",
                "send-enter"
            },
            events);
    }

    [Fact]
    public async Task SubmitFolderNavigationWithAddressBarFocusesDialogThenAddressBarBeforeSendingPath()
    {
        var dialogHandle = new IntPtr(42);
        var events = new List<string>();

        var result = await WindowsDialogAutomation.SubmitFolderNavigationWithAddressBarAsync(
            dialogHandle,
            "C:\\Users\\paulx\\Downloads",
            () =>
            {
                events.Add("focus-dialog");
                return true;
            },
            () =>
            {
                events.Add("focus-address-bar");
                return true;
            },
            () =>
            {
                events.Add("confirm-address-bar-focus");
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
            });

        Assert.True(result);
        Assert.Equal(
            new[]
            {
                "focus-dialog",
                "focus-address-bar",
                "confirm-address-bar-focus",
                "send-path:C:\\Users\\paulx\\Downloads",
                "send-enter"
            },
            events);
    }

    [Fact]
    public async Task SubmitFolderNavigationWithAddressBarSkipsTypingWhenAddressBarFocusCannotBeConfirmed()
    {
        var events = new List<string>();

        var result = await WindowsDialogAutomation.SubmitFolderNavigationWithAddressBarAsync(
            new IntPtr(42),
            "C:\\Users\\paulx\\Downloads",
            () =>
            {
                events.Add("focus-dialog");
                return true;
            },
            () =>
            {
                events.Add("focus-address-bar");
                return true;
            },
            () =>
            {
                events.Add("confirm-address-bar-focus");
                return false;
            },
            _ =>
            {
                events.Add("send-path");
                return true;
            },
            () =>
            {
                events.Add("send-enter");
                return true;
            });

        Assert.False(result);
        Assert.Equal(new[] { "focus-dialog", "focus-address-bar", "confirm-address-bar-focus" }, events);
    }

    [Fact]
    public void TryConfirmAddressBarFocusAcceptsFocusedToolbarDescendant()
    {
        var dialogHandle = new IntPtr(100);
        var toolbarHandle = new IntPtr(200);
        var focusedEditHandle = new IntPtr(300);

        var result = WindowsDialogAutomation.TryConfirmAddressBarFocus(
            dialogHandle,
            () => dialogHandle,
            _ => IntPtr.Zero,
            (_, _) => toolbarHandle,
            () => focusedEditHandle,
            handle => handle == focusedEditHandle ? toolbarHandle : IntPtr.Zero);

        Assert.True(result);
    }

    [Fact]
    public void TryConfirmAddressBarFocusRejectsUnrelatedForegroundWindow()
    {
        var dialogHandle = new IntPtr(100);
        var unrelatedWindow = new IntPtr(200);

        var result = WindowsDialogAutomation.TryConfirmAddressBarFocus(
            dialogHandle,
            () => unrelatedWindow,
            _ => IntPtr.Zero,
            (_, _) => new IntPtr(300),
            () => new IntPtr(400),
            _ => IntPtr.Zero);

        Assert.False(result);
    }

    [Fact]
    public void TryConfirmAddressBarFocusRejectsMissingAddressToolbar()
    {
        var dialogHandle = new IntPtr(100);

        var result = WindowsDialogAutomation.TryConfirmAddressBarFocus(
            dialogHandle,
            () => dialogHandle,
            _ => IntPtr.Zero,
            (_, _) => IntPtr.Zero,
            () => new IntPtr(300),
            _ => IntPtr.Zero);

        Assert.False(result);
    }

    [Fact]
    public async Task TryFolderNavigationStrategiesContinuesToAddressBarWhenKeyboardStrategyFails()
    {
        var events = new List<string>();

        var result = await WindowsDialogAutomation.TryFolderNavigationStrategiesAsync(
            () =>
            {
                events.Add("keyboard");
                return Task.FromResult(false);
            },
            () =>
            {
                events.Add("address-bar");
                return Task.FromResult(true);
            });

        Assert.True(result);
        Assert.Equal(new[] { "keyboard", "address-bar" }, events);
    }

    [Fact]
    public async Task TryFolderNavigationStrategiesStopsAfterKeyboardStrategySucceeds()
    {
        var events = new List<string>();

        var result = await WindowsDialogAutomation.TryFolderNavigationStrategiesAsync(
            () =>
            {
                events.Add("keyboard");
                return Task.FromResult(true);
            },
            () =>
            {
                events.Add("address-bar");
                return Task.FromResult(true);
            });

        Assert.True(result);
        Assert.Equal(new[] { "keyboard" }, events);
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
    public void TryFocusFileNameEditWindowAttachesInputQueuesWhenThreadsDiffer()
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
            () => dialogHandle,
            () => fileNameEditHandle,
            _ => IntPtr.Zero,
            () => events.Add("wait"),
            getCurrentThreadId: () => 10,
            getWindowThreadId: _ => 20,
            attachThreadInput: (fromThread, toThread, attach) =>
            {
                events.Add($"attach:{fromThread}:{toThread}:{attach}");
                return true;
            });

        Assert.True(result);
        Assert.Contains("attach:10:20:True", events);
        Assert.Contains("attach:10:20:False", events);
        Assert.True(events.IndexOf("attach:10:20:True") < events.IndexOf("set-foreground:" + dialogHandle));
    }

    [Fact]
    public void TryFindPathEditWindowHandleRejectsFolderPickerEditControlForCommitStrategy()
    {
        var dialogHandle = new IntPtr(100);
        var folderEditHandle = new IntPtr(200);

        var result = WindowsDialogAutomation.TryFindPathEditWindowHandle(
            dialogHandle,
            getDlgItem: (_, controlId) => controlId == 1152 ? folderEditHandle : IntPtr.Zero,
            childWindowProvider: _ => Array.Empty<IntPtr>(),
            classNameProvider: handle => handle == folderEditHandle ? "Edit" : string.Empty,
            out var resolvedHandle);

        Assert.False(result);
        Assert.Equal(IntPtr.Zero, resolvedHandle);
    }

    [Fact]
    public void TryFindAddressBarEditWindowHandleFindsNestedAddressEditControl()
    {
        var dialogHandle = new IntPtr(100);
        var addressEditHandle = new IntPtr(200);
        var folderEditHandle = new IntPtr(300);

        var result = WindowsDialogAutomation.TryFindAddressBarEditWindowHandle(
            dialogHandle,
            childWindowProvider: _ => new[] { folderEditHandle, addressEditHandle },
            classNameProvider: handle => handle == addressEditHandle || handle == folderEditHandle
                ? "Edit"
                : string.Empty,
            controlIdProvider: handle => handle == addressEditHandle
                ? 41477
                : handle == folderEditHandle
                    ? 1152
                    : 0,
            out var resolvedHandle);

        Assert.True(result);
        Assert.Equal(addressEditHandle, resolvedHandle);
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
    public void ResolveActiveDialogHandleIgnoresOwnerlessDialogWhenListaryIsForeground()
    {
        var searchPanel = new IntPtr(10);
        var ownerlessDialog = new IntPtr(20);

        var resolved = WindowsDialogAutomation.ResolveActiveDialogHandle(
            searchPanel,
            handle => handle == searchPanel ? "HwndWrapper[ListaryOpen.App;;]" : "#32770",
            handle => handle == searchPanel ? "ListaryOpen.App" : "unknown",
            _ => IntPtr.Zero,
            () => new[] { ownerlessDialog });

        Assert.Equal(IntPtr.Zero, resolved);
    }

    [Fact]
    public void ResolveActiveDialogHandleFindsBrowserDialogOwnedByAnotherSameProcessWindow()
    {
        var foregroundBrowser = new IntPtr(10);
        var browserDialog = new IntPtr(20);
        var browserWindow = new IntPtr(30);

        var resolved = WindowsDialogAutomation.ResolveActiveDialogHandle(
            foregroundBrowser,
            handle => handle == foregroundBrowser || handle == browserWindow ? "MozillaWindowClass" : "#32770",
            _ => "firefox",
            handle => handle == browserDialog ? browserWindow : IntPtr.Zero,
            () => new[] { browserDialog, browserWindow });

        Assert.Equal(browserDialog, resolved);
    }

    [Fact]
    public void ResolveActiveDialogHandleIgnoresBrowserDialogOwnedByDifferentSameNameProcess()
    {
        var foregroundBrowser = new IntPtr(10);
        var browserDialog = new IntPtr(20);
        var browserWindow = new IntPtr(30);
        var processIds = new Dictionary<IntPtr, uint>
        {
            [foregroundBrowser] = 100,
            [browserDialog] = 200,
            [browserWindow] = 200
        };

        var resolved = WindowsDialogAutomation.ResolveActiveDialogHandle(
            foregroundBrowser,
            handle => handle == foregroundBrowser || handle == browserWindow ? "MozillaWindowClass" : "#32770",
            _ => "firefox",
            handle => handle == browserDialog ? browserWindow : IntPtr.Zero,
            () => new[] { browserDialog, browserWindow },
            processIdProvider: handle => processIds.TryGetValue(handle, out var processId) ? processId : null,
            childControlClassNamesProvider: handle => handle == browserDialog
                ? new[] { "DirectUIHWND1", "ToolbarWindow321", "Edit1" }
                : Array.Empty<string>());

        Assert.Equal(IntPtr.Zero, resolved);
    }

    [Fact]
    public void ResolveActiveDialogHandlePrefersForegroundOwnedDialog()
    {
        var foregroundBrowser = new IntPtr(10);
        var foregroundOwnedDialog = new IntPtr(20);
        var backgroundDialog = new IntPtr(30);
        var backgroundWindow = new IntPtr(40);

        var resolved = WindowsDialogAutomation.ResolveActiveDialogHandle(
            foregroundBrowser,
            handle => handle == foregroundBrowser || handle == backgroundWindow ? "MozillaWindowClass" : "#32770",
            _ => "firefox",
            handle => handle == foregroundOwnedDialog
                ? foregroundBrowser
                : handle == backgroundDialog
                    ? backgroundWindow
                    : IntPtr.Zero,
            () => new[] { backgroundDialog, backgroundWindow, foregroundOwnedDialog });

        Assert.Equal(foregroundOwnedDialog, resolved);
    }

    [Fact]
    public void ResolveActiveDialogHandleIgnoresBrowserForegroundWhenMultipleSameProcessDialogsExist()
    {
        var foregroundBrowser = new IntPtr(10);
        var firstDialog = new IntPtr(20);
        var firstWindow = new IntPtr(30);
        var secondDialog = new IntPtr(40);
        var secondWindow = new IntPtr(50);

        var resolved = WindowsDialogAutomation.ResolveActiveDialogHandle(
            foregroundBrowser,
            handle => handle == foregroundBrowser || handle == firstWindow || handle == secondWindow
                ? "MozillaWindowClass"
                : "#32770",
            _ => "firefox",
            handle => handle == firstDialog
                ? firstWindow
                : handle == secondDialog
                    ? secondWindow
                    : IntPtr.Zero,
            () => new[] { firstDialog, firstWindow, secondDialog, secondWindow });

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
    public void ResolveActiveDialogHandleDoesNotApplyStandardShapeFilterToMozillaDialogClass()
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
                    : "MozillaDialogClass",
            handle => handle == searchPanel
                ? "ListaryOpen.App"
                : "firefox",
            handle => handle == firefoxDialog ? firefoxWindow : IntPtr.Zero,
            () => new[] { firefoxDialog, firefoxWindow },
            processIdProvider: _ => 100,
            childControlClassNamesProvider: _ => new[] { "MozillaCompositorWindowClass" });

        Assert.Equal(firefoxDialog, resolved);
    }

    [Fact]
    public void ResolveActiveDialogHandleFindsSingleStandardFileDialogWhenListaryIsForeground()
    {
        var searchPanel = new IntPtr(10);
        var notepadPlusPlusDialog = new IntPtr(20);
        var notepadPlusPlusWindow = new IntPtr(30);

        var resolved = WindowsDialogAutomation.ResolveActiveDialogHandle(
            searchPanel,
            handle => handle == searchPanel
                ? "HwndWrapper[ListaryOpen.App;;]"
                : handle == notepadPlusPlusWindow
                    ? "Notepad++"
                    : "#32770",
            handle => handle == searchPanel
                ? "ListaryOpen.App"
                : "notepad++",
            handle => handle == notepadPlusPlusDialog ? notepadPlusPlusWindow : IntPtr.Zero,
            () => new[] { notepadPlusPlusDialog, notepadPlusPlusWindow });

        Assert.Equal(notepadPlusPlusDialog, resolved);
    }

    [Fact]
    public void ResolveActiveDialogHandleIgnoresNonFileStandardDialogsWhenListaryIsForeground()
    {
        var searchPanel = new IntPtr(10);
        var notepadPlusPlusDialog = new IntPtr(20);
        var notepadPlusPlusWindow = new IntPtr(30);
        var messageBoxDialog = new IntPtr(40);
        var messageBoxOwner = new IntPtr(50);
        var processIds = new Dictionary<IntPtr, uint>
        {
            [searchPanel] = 100,
            [notepadPlusPlusDialog] = 200,
            [notepadPlusPlusWindow] = 200,
            [messageBoxDialog] = 300,
            [messageBoxOwner] = 300
        };

        var resolved = WindowsDialogAutomation.ResolveActiveDialogHandle(
            searchPanel,
            handle => handle == searchPanel
                ? "HwndWrapper[ListaryOpen.App;;]"
                : handle == notepadPlusPlusWindow
                    ? "Notepad++"
                    : "#32770",
            handle => handle == searchPanel
                ? "ListaryOpen.App"
                : handle == notepadPlusPlusDialog || handle == notepadPlusPlusWindow
                    ? "notepad++"
                    : "example",
            handle => handle == notepadPlusPlusDialog
                ? notepadPlusPlusWindow
                : handle == messageBoxDialog
                    ? messageBoxOwner
                    : IntPtr.Zero,
            () => new[] { notepadPlusPlusDialog, notepadPlusPlusWindow, messageBoxDialog, messageBoxOwner },
            processIdProvider: handle => processIds.TryGetValue(handle, out var processId) ? processId : null,
            childControlClassNamesProvider: handle => handle == notepadPlusPlusDialog
                ? new[] { "DirectUIHWND1", "ToolbarWindow321", "Edit1" }
                : handle == messageBoxDialog
                    ? new[] { "Static1", "Button1" }
                    : Array.Empty<string>());

        Assert.Equal(notepadPlusPlusDialog, resolved);
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

    [Fact]
    public void ResolveActiveDialogHandleIgnoresListaryForegroundWhenMultipleFileDialogsExist()
    {
        var searchPanel = new IntPtr(10);
        var firefoxDialog = new IntPtr(20);
        var firefoxWindow = new IntPtr(30);
        var notepadPlusPlusDialog = new IntPtr(40);
        var notepadPlusPlusWindow = new IntPtr(50);

        var resolved = WindowsDialogAutomation.ResolveActiveDialogHandle(
            searchPanel,
            handle => handle == searchPanel
                ? "HwndWrapper[ListaryOpen.App;;]"
                : handle == firefoxWindow
                    ? "MozillaWindowClass"
                    : handle == notepadPlusPlusWindow
                        ? "Notepad++"
                        : "#32770",
            handle => handle == searchPanel
                ? "ListaryOpen.App"
                : handle == firefoxDialog || handle == firefoxWindow
                    ? "firefox"
                    : "notepad++",
            handle => handle == firefoxDialog
                ? firefoxWindow
                : handle == notepadPlusPlusDialog
                    ? notepadPlusPlusWindow
                    : IntPtr.Zero,
            () => new[] { firefoxDialog, firefoxWindow, notepadPlusPlusDialog, notepadPlusPlusWindow });

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
    public void IsFileNameControlCandidateSupportsProbeFallbackForFirefoxUploadEditPane()
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
    public void IsCommitButtonCandidateSupportsProbeFallbackForFirefoxUploadOpenButtonPane()
    {
        Assert.True(WindowsDialogAutomation.IsCommitButtonCandidate(
            "1",
            "Open",
            "Button",
            "ControlType.Pane"));
    }
}
