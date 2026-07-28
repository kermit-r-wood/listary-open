using WpfApp = ListaryOpen.App.App;
using ListaryOpen.App;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class ExplorerTypeSearchTests
{
    [Theory]
    [InlineData(GlobalPointerButton.Left, true)]
    [InlineData(GlobalPointerButton.Middle, true)]
    [InlineData(GlobalPointerButton.Other, true)]
    [InlineData(GlobalPointerButton.Right, false)]
    public void PointerClosesAnExistingResultMenuWithoutCancelingANewRightClick(
        GlobalPointerButton button,
        bool expected)
    {
        Assert.Equal(expected, WpfApp.ShouldCloseResultContextMenus(button));
    }

    [Theory]
    [InlineData("Taskmgr", true)]
    [InlineData("taskmgr.exe", true)]
    [InlineData("explorer", false)]
    [InlineData("", false)]
    public void TaskManagerHostRequiresTheRealProcessName(string processName, bool expected)
    {
        Assert.Equal(expected, GlobalTextInputService.IsTaskManagerProcessName(processName));
    }
    [Theory]
    [InlineData("CabinetWClass", true)]
    [InlineData("ExploreWClass", true)]
    [InlineData("Chrome_WidgetWin_1", false)]
    [InlineData("ApplicationFrameWindow", false)]
    public void GlobalTextCaptureIsRestrictedToExplorerTopLevelWindows(string className, bool expected)
    {
        Assert.Equal(expected, GlobalTextInputService.IsExplorerTopLevelWindowClass(className));
    }

    [Fact]
    public void DialogTextCaptureIsRestrictedToTheCurrentlyAttachedDialog()
    {
        var attachedDialog = new IntPtr(42);

        Assert.True(GlobalTextInputService.IsConfiguredDialogInputWindow(attachedDialog, attachedDialog));
        Assert.False(GlobalTextInputService.IsConfiguredDialogInputWindow(new IntPtr(41), attachedDialog));
        Assert.False(GlobalTextInputService.IsConfiguredDialogInputWindow(IntPtr.Zero, IntPtr.Zero));
    }

    [Fact]
    public void DialogTextCaptureAcceptsChildAndOwnedPopupWindowsFromTheAttachedDialog()
    {
        var dialog = new IntPtr(42);
        var child = new IntPtr(43);
        var popup = new IntPtr(44);
        var unrelated = new IntPtr(45);
        IntPtr Root(IntPtr window) => window == child ? dialog : window;
        IntPtr Owner(IntPtr window) => window == popup ? dialog : IntPtr.Zero;

        Assert.True(GlobalTextInputService.IsConfiguredDialogInputWindow(child, dialog, Root, Owner));
        Assert.True(GlobalTextInputService.IsConfiguredDialogInputWindow(popup, dialog, Root, Owner));
        Assert.False(GlobalTextInputService.IsConfiguredDialogInputWindow(unrelated, dialog, Root, Owner));
    }

    [Fact]
    public void RapidTextInputIsCoalescedInOriginalOrderPerExplorerWindow()
    {
        var firstExplorer = new IntPtr(42);
        var secondExplorer = new IntPtr(84);
        var coalesced = WpfApp.CoalesceGlobalTextInputs(new[]
        {
            new GlobalTextInputEventArgs("o", firstExplorer, "DirectUIHWND"),
            new GlobalTextInputEventArgs("p", firstExplorer, "DirectUIHWND"),
            new GlobalTextInputEventArgs("e", firstExplorer, "DirectUIHWND"),
            new GlobalTextInputEventArgs("n", firstExplorer, "DirectUIHWND"),
            new GlobalTextInputEventArgs("x", secondExplorer, "DirectUIHWND")
        });

        Assert.Equal(2, coalesced.Count);
        Assert.Equal("open", coalesced[0].Text);
        Assert.Equal(firstExplorer, coalesced[0].ForegroundWindow);
        Assert.Equal("x", coalesced[1].Text);
        Assert.Equal(secondExplorer, coalesced[1].ForegroundWindow);
    }

    [Fact]
    public void DialogInputIsCoalescedWithoutBeingMergedWithExplorerInput()
    {
        var window = new IntPtr(42);
        var coalesced = WpfApp.CoalesceGlobalTextInputs(new[]
        {
            new GlobalTextInputEventArgs("o", window, "DirectUIHWND", GlobalTextInputHost.Dialog),
            new GlobalTextInputEventArgs("n", window, "DirectUIHWND", GlobalTextInputHost.Dialog),
            new GlobalTextInputEventArgs("x", window, "DirectUIHWND", GlobalTextInputHost.Explorer)
        });

        Assert.Equal(2, coalesced.Count);
        Assert.Equal("on", coalesced[0].Text);
        Assert.Equal(GlobalTextInputHost.Dialog, coalesced[0].Host);
        Assert.Equal("x", coalesced[1].Text);
        Assert.Equal(GlobalTextInputHost.Explorer, coalesced[1].Host);
    }

    [Theory]
    [InlineData(GlobalTextInputHost.Dialog, true)]
    [InlineData(GlobalTextInputHost.Explorer, false)]
    [InlineData(GlobalTextInputHost.TaskManager, true)]
    public void DialogAndTaskManagerConsumeTextBeforeNativeUiCanStealFocus(
        GlobalTextInputHost host,
        bool expected)
    {
        Assert.Equal(expected, WpfApp.ShouldConsumeGlobalTextInput(host));
    }

    [Fact]
    public void ExpandedDialogQuickSwitchEnablesFullOverlayKeyboardCapture()
    {
        Assert.True(WpfApp.ShouldCaptureOverlayInput(
            dialogQuickSwitchExpanded: true,
            explorerQuickMenuOpen: false,
            taskManagerSearchActive: false,
            resultContextMenuOpen: false,
            explorerTypeSearchActive: false));
        Assert.False(WpfApp.ShouldCaptureOverlayInput(false, false, false, false, false));
    }

    [Theory]
    [InlineData(" ", false, false)]
    [InlineData(" ", true, true)]
    [InlineData("\t", true, false)]
    [InlineData("a", false, true)]
    [InlineData("中", false, true)]
    public void TranslatedWhitespaceIsCapturedOnlyDuringAnActiveOverlaySession(
        string text,
        bool allowWhitespace,
        bool expected)
    {
        Assert.Equal(
            expected,
            GlobalTextInputService.IsSupportedTranslatedText(text, allowWhitespace));
    }

    [Fact]
    public void DialogQuickSwitchCommandsRequireTheActiveAttachedDialog()
    {
        var dialog = new IntPtr(42);
        var overlay = new IntPtr(84);

        Assert.True(WpfApp.ShouldHandleDialogQuickSwitchInput(dialog, dialog));
        Assert.True(WpfApp.ShouldHandleDialogQuickSwitchInput(overlay, dialog, overlay));
        Assert.False(WpfApp.ShouldHandleDialogQuickSwitchInput(new IntPtr(41), dialog));
        Assert.False(WpfApp.ShouldHandleDialogQuickSwitchInput(new IntPtr(41), dialog, overlay));
        Assert.False(WpfApp.ShouldHandleDialogQuickSwitchInput(dialog, IntPtr.Zero));
    }

    [Fact]
    public void FollowUpInputFromSameExplorerAlwaysAppendsWhileSearchPanelIsVisible()
    {
        var explorerWindow = new IntPtr(42);
        var input = new GlobalTextInputEventArgs("e", explorerWindow, "DirectUIHWND");

        var shouldAppend = WpfApp.ShouldAppendExplorerTypeSearchInput(
            input,
            explorerWindow,
            explorerSearchPanelVisible: true);

        Assert.True(shouldAppend);
    }

    [Theory]
    [InlineData(false, 42)]
    [InlineData(true, 41)]
    public void FollowUpInputDoesNotAppendOutsideActiveExplorerSession(
        bool panelVisible,
        int inputWindow)
    {
        var explorerWindow = new IntPtr(42);
        var input = new GlobalTextInputEventArgs("e", new IntPtr(inputWindow), "DirectUIHWND");

        Assert.False(WpfApp.ShouldAppendExplorerTypeSearchInput(
            input,
            explorerWindow,
            panelVisible));
    }

    [Fact]
    public void ForegroundExplorerTypingCreatesSearchRequestForItsCurrentFolder()
    {
        var explorerWindow = new IntPtr(42);
        var input = new GlobalTextInputEventArgs("r", explorerWindow, "DirectUIHWND");
        var provider = new FixedWindowProvider(
            new QuickSwitchFolderCandidate("C:\\Projects", "Explorer", explorerWindow, true));

        var request = WpfApp.TryCreateExplorerTypeSearchRequest(input, provider);

        Assert.NotNull(request);
        Assert.Equal("r", request!.InitialQuery);
        Assert.Equal("C:\\Projects", request.CurrentFolder);
        Assert.Equal(explorerWindow, request.ExplorerWindow);
    }

    [Theory]
    [InlineData("Edit")]
    [InlineData("RichEditD2DPT")]
    [InlineData("SearchBoxControl")]
    [InlineData("Windows.UI.TextBox")]
    public void TextEntryFocusDoesNotTriggerExplorerSearch(string focusedControlClass)
    {
        var explorerWindow = new IntPtr(42);
        var input = new GlobalTextInputEventArgs("r", explorerWindow, focusedControlClass);
        var provider = new FixedWindowProvider(
            new QuickSwitchFolderCandidate("C:\\Projects", "Explorer", explorerWindow, true));

        Assert.Null(WpfApp.TryCreateExplorerTypeSearchRequest(input, provider));
        Assert.True(GlobalTextInputService.IsTextEntryControlClass(focusedControlClass));
    }

    [Theory]
    [InlineData(GlobalTextInputHost.Explorer, 42, 42, 1000, 1100, true)]
    [InlineData(GlobalTextInputHost.Explorer, 42, 42, 1000, 1750, true)]
    [InlineData(GlobalTextInputHost.Explorer, 42, 42, 1000, 1751, false)]
    [InlineData(GlobalTextInputHost.Explorer, 42, 41, 1000, 1100, false)]
    [InlineData(GlobalTextInputHost.Dialog, 42, 42, 1000, 1100, false)]
    public void RightClickMenuAcceleratorsAreNotCapturedAsExplorerTypeSearch(
        GlobalTextInputHost host,
        int foregroundWindow,
        int rightClickWindow,
        long rightClickTick,
        long currentTick,
        bool expected)
    {
        Assert.Equal(
            expected,
            GlobalTextInputService.ShouldSuppressTextAfterRightClick(
                host,
                new IntPtr(foregroundWindow),
                rightClickTick,
                new IntPtr(rightClickWindow),
                currentTick));
    }

    [Theory]
    [InlineData(GlobalTextInputHost.Dialog, "Edit", 1148, true)]
    [InlineData(GlobalTextInputHost.Dialog, "Edit", 1152, true)]
    [InlineData(GlobalTextInputHost.Dialog, "Edit", 41477, false)]
    [InlineData(GlobalTextInputHost.Dialog, "SearchBoxControl", 0, false)]
    [InlineData(GlobalTextInputHost.Explorer, "Edit", 1148, false)]
    [InlineData(GlobalTextInputHost.Dialog, "DirectUIHWND", 0, true)]
    public void DialogTypingCanStartQuickSwitchFromTheDefaultFileNameControl(
        GlobalTextInputHost host,
        string focusedControlClass,
        int focusedControlId,
        bool expected)
    {
        Assert.Equal(
            expected,
            GlobalTextInputService.ShouldCaptureTextInput(
                host,
                focusedControlClass,
                focusedControlId));
    }

    [Fact]
    public void CapturedExplorerWindowHandleTriggersSearchEvenIfSnapshotForegroundMoved()
    {
        // Key was captured for window 42; by the time the snapshot is read another
        // Explorer may be marked foreground. Still open search for the typed window.
        var input = new GlobalTextInputEventArgs("r", new IntPtr(42), "DirectUIHWND");
        var provider = new FixedWindowProvider(
            new QuickSwitchFolderCandidate("C:\\Projects", "Explorer", new IntPtr(41), true),
            new QuickSwitchFolderCandidate("C:\\Docs", "Explorer", new IntPtr(42), false));

        var request = WpfApp.TryCreateExplorerTypeSearchRequest(input, provider);

        Assert.NotNull(request);
        Assert.Equal("C:\\Docs", request!.CurrentFolder);
        Assert.Equal(new IntPtr(42), request.ExplorerWindow);
    }

    [Fact]
    public void UnrelatedExplorerWindowDoesNotTriggerSearch()
    {
        var input = new GlobalTextInputEventArgs("r", new IntPtr(42), "DirectUIHWND");
        var provider = new FixedWindowProvider(
            new QuickSwitchFolderCandidate("C:\\Projects", "Explorer", new IntPtr(41), true));

        Assert.Null(WpfApp.TryCreateExplorerTypeSearchRequest(input, provider));
    }

    [Fact]
    public void EscapeFromSameExplorerDismissesVisibleTypeSearch()
    {
        var explorerWindow = new IntPtr(42);
        var input = new GlobalEscapeInputEventArgs(explorerWindow, "DirectUIHWND");

        Assert.True(WpfApp.ShouldDismissExplorerTypeSearch(
            input,
            explorerWindow,
            explorerSearchPanelVisible: true));
    }

    [Theory]
    [InlineData(false, 42, "DirectUIHWND")]
    [InlineData(true, 41, "DirectUIHWND")]
    [InlineData(true, 42, "Edit")]
    public void EscapeDoesNotDismissUnrelatedExplorerSession(
        bool panelVisible,
        int inputWindow,
        string focusedControlClass)
    {
        var input = new GlobalEscapeInputEventArgs(new IntPtr(inputWindow), focusedControlClass);

        Assert.False(WpfApp.ShouldDismissExplorerTypeSearch(
            input,
            new IntPtr(42),
            panelVisible));
    }

    [Theory]
    [InlineData(true, true, 1)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 2)]
    [InlineData(false, false, 0)]
    public void EscapeTargetsOnlyTheActiveOverlay(
        bool taskManagerSearchActive,
        bool explorerSearchActive,
        int expected)
    {
        Assert.Equal(
            expected,
            (int)WpfApp.GetOverlayEscapeTarget(taskManagerSearchActive, explorerSearchActive));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void PointerDismissesOnlyVisibleExplorerSearchWhenPressedOutside(
        bool panelVisible,
        bool pointerInsidePanel,
        bool expected)
    {
        Assert.Equal(
            expected,
            WpfApp.ShouldDismissExplorerTypeSearchForPointer(panelVisible, pointerInsidePanel));
    }

    [Theory]
    [InlineData(true, false, true, 42, 42, 100, false)]
    [InlineData(true, false, true, 100, 42, 100, false)]
    [InlineData(true, false, true, 0, 42, 100, false)]
    [InlineData(true, false, true, 200, 42, 100, true)]
    [InlineData(true, true, true, 200, 42, 100, false)]
    [InlineData(true, false, false, 200, 42, 100, false)]
    public void ExplorerSearchSurvivesProgrammaticFocusReturnToItsExplorer(
        bool isVisible,
        bool isActive,
        bool isExplorerSearchMode,
        int foregroundWindow,
        int explorerWindow,
        int overlayWindow,
        bool expected)
    {
        Assert.Equal(
            expected,
            SearchPanel.ShouldDismissExplorerSearchAfterDeactivation(
                isVisible,
                isActive,
                isExplorerSearchMode,
                new IntPtr(foregroundWindow),
                new IntPtr(explorerWindow),
                new IntPtr(overlayWindow)));
    }

    [Fact]
    public void NavigationFromSameExplorerIsRoutedToVisibleOverlay()
    {
        var explorerWindow = new IntPtr(42);
        var input = new GlobalNavigationInputEventArgs(1, explorerWindow, "DirectUIHWND");

        Assert.True(WpfApp.ShouldHandleExplorerTypeSearchNavigation(
            input,
            explorerWindow,
            explorerSearchPanelVisible: true));
    }

    [Fact]
    public void NavigationCaptureSynchronouslyHandlesTheActiveExplorerSession()
    {
        var explorerWindow = new IntPtr(42);
        var capture = new ExplorerTypeSearchNavigationCapture();
        var activeSession = capture.Activate(explorerWindow);
        var input = new GlobalNavigationInputEventArgs(-1, explorerWindow, "DirectUIHWND");

        var capturedSession = capture.TryCapture(input);

        Assert.Same(activeSession, capturedSession);
        Assert.True(capture.IsCurrent(activeSession));
        Assert.True(input.Handled);
    }

    [Fact]
    public void EditCommandCaptureSynchronouslyHandlesTheActiveExplorerSession()
    {
        var explorerWindow = new IntPtr(42);
        var capture = new ExplorerTypeSearchNavigationCapture();
        var activeSession = capture.Activate(explorerWindow);
        var input = new GlobalEditCommandInputEventArgs(
            GlobalEditCommand.SelectAll,
            explorerWindow,
            "DirectUIHWND");

        Assert.Same(activeSession, capture.TryCapture(input));
        Assert.True(input.Handled);
    }

    [Theory]
    [InlineData(0x41, GlobalEditCommand.SelectAll)]
    [InlineData(0x43, GlobalEditCommand.Copy)]
    [InlineData(0x58, GlobalEditCommand.Cut)]
    public void OverlayRecognizesStandardControlEditCommands(uint virtualKey, GlobalEditCommand expected)
    {
        Assert.Equal(
            expected,
            GlobalTextInputService.GetOverlayEditCommand(
                virtualKey,
                controlDown: true,
                shiftDown: false,
                altDown: false,
                windowsDown: false));
    }

    [Fact]
    public void OverlayRecognizesBackspaceWithoutCommandModifiers()
    {
        Assert.Equal(
            GlobalEditCommand.Backspace,
            GlobalTextInputService.GetOverlayEditCommand(
                0x08,
                controlDown: false,
                shiftDown: false,
                altDown: false,
                windowsDown: false));
    }

    [Theory]
    [InlineData(0x1B, true, false, true)]
    [InlineData(0x1B, false, false, false)]
    [InlineData(0x1B, true, true, false)]
    [InlineData(0x0D, true, false, false)]
    public void OverlayCapturesPlainEscapeSoItDoesNotReachTheHostWindow(
        uint virtualKey,
        bool captureOverlayInput,
        bool hasCommandModifier,
        bool expected)
    {
        Assert.Equal(
            expected,
            GlobalTextInputService.ShouldCaptureOverlayEscape(
                virtualKey,
                captureOverlayInput,
                hasCommandModifier));
    }

    [Theory]
    [InlineData(0x31, 0)]
    [InlineData(0x39, 8)]
    [InlineData(0x61, 0)]
    [InlineData(0x69, 8)]
    public void OverlayRecognizesControlNumberResultShortcuts(uint virtualKey, int expectedIndex)
    {
        Assert.Equal(
            expectedIndex,
            GlobalTextInputService.GetOverlayResultShortcutIndex(
                virtualKey,
                controlDown: true,
                shiftDown: false,
                altDown: false,
                windowsDown: false));
    }

    [Fact]
    public void ResultShortcutCaptureSynchronouslyHandlesTheActiveExplorerSession()
    {
        var explorerWindow = new IntPtr(42);
        var capture = new ExplorerTypeSearchNavigationCapture();
        var activeSession = capture.Activate(explorerWindow);
        var input = new GlobalResultShortcutInputEventArgs(2, explorerWindow, "DirectUIHWND");

        Assert.Same(activeSession, capture.TryCapture(input));
        Assert.True(input.Handled);
    }

    [Theory]
    [InlineData(0x41, false, false, false, false)]
    [InlineData(0x41, true, true, false, false)]
    [InlineData(0x41, true, false, true, false)]
    [InlineData(0x41, true, false, false, true)]
    [InlineData(0x56, true, false, false, false)]
    public void OverlayDoesNotCaptureUnrequestedModifiedKeys(
        uint virtualKey,
        bool controlDown,
        bool shiftDown,
        bool altDown,
        bool windowsDown)
    {
        Assert.Null(GlobalTextInputService.GetOverlayEditCommand(
            virtualKey,
            controlDown,
            shiftDown,
            altDown,
            windowsDown));
    }

    [Fact]
    public void NavigationCaptureDoesNotHandleAnInactiveOverlay()
    {
        var explorerWindow = new IntPtr(42);
        var capture = new ExplorerTypeSearchNavigationCapture();
        capture.Activate(explorerWindow);
        capture.Deactivate();
        var input = new GlobalNavigationInputEventArgs(1, explorerWindow, "DirectUIHWND");

        Assert.Null(capture.TryCapture(input));
        Assert.False(input.Handled);
    }

    [Fact]
    public void NavigationQueuedDuringCloseCannotMoveAReplacementSessionForTheSameExplorer()
    {
        var explorerWindow = new IntPtr(42);
        var capture = new ExplorerTypeSearchNavigationCapture();
        var closingSession = capture.Activate(explorerWindow);
        var input = new GlobalNavigationInputEventArgs(1, explorerWindow, "DirectUIHWND");

        Assert.Same(closingSession, capture.TryCapture(input));

        capture.Deactivate();
        var replacementSession = capture.Activate(explorerWindow);

        Assert.False(capture.IsCurrent(closingSession));
        Assert.True(capture.IsCurrent(replacementSession));
    }

    [Theory]
    [InlineData(false, 42, "DirectUIHWND")]
    [InlineData(true, 41, "DirectUIHWND")]
    [InlineData(true, 42, "Edit")]
    public void NavigationDoesNotAffectExplorerOutsideActiveOverlay(
        bool panelVisible,
        int inputWindow,
        string focusedControlClass)
    {
        var input = new GlobalNavigationInputEventArgs(-1, new IntPtr(inputWindow), focusedControlClass);

        Assert.False(WpfApp.ShouldHandleExplorerTypeSearchNavigation(
            input,
            new IntPtr(42),
            panelVisible));
    }

    private sealed class FixedWindowProvider : IQuickSwitchWindowProvider
    {
        private readonly IReadOnlyList<QuickSwitchFolderCandidate> _candidates;

        public FixedWindowProvider(params QuickSwitchFolderCandidate[] candidates)
        {
            _candidates = candidates;
        }

        public IReadOnlyList<QuickSwitchFolderCandidate> GetFolderCandidates() => _candidates;
    }
}
