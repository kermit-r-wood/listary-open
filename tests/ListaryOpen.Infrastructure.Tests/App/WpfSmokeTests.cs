using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ListaryOpen.App;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Core.Settings;
using ListaryOpen.Infrastructure.Dialog;
using ListaryOpen.Infrastructure.Windows;
using WpfApp = ListaryOpen.App.App;

namespace ListaryOpen.Infrastructure.Tests.App;

[Collection(WpfTestCollection.Name)]
public sealed class WpfSmokeTests
{
    [Fact]
    public void UiResourcesAndWindowsLoadOnStaThread()
    {
        Exception? exception = null;
        var completed = false;
        var stage = "not started";
        var thread = new Thread(() =>
        {
            WpfApp? application = null;
            MainWindow? mainWindow = null;
            SearchPanel? searchPanel = null;
            QuickSwitchBarWindow? quickSwitchBar = null;
            TaskManagerSearchWindow? taskManagerSearchWindow = null;
            TaskManagerSearchWindow? taskManagerRetryWindow = null;
            Window? layoutProbeWindow = null;

            try
            {
                Volatile.Write(ref stage, "initializing application");
                application = new WpfApp();
                application.InitializeComponent();
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

                Assert.NotNull(application.FindResource("Brush.AppBackground"));
                Assert.NotNull(application.FindResource("Brush.Surface"));
                Assert.NotNull(application.FindResource("Brush.TextPrimary"));
                Assert.NotNull(application.FindResource("SearchPanelResultListStyle"));
                Assert.NotNull(application.FindResource("SettingsSectionStyle"));

                ThemeManager.Apply(AppTheme.Light);
                LocalizationManager.Apply(AppLanguage.English);
                var settingsViewModel = new SettingsViewModel(
                    AppSettings.Defaults(),
                    enableNtfsFastIndexing: null,
                    enableHookQuickSwitch: null,
                    applySettings: settings =>
                    {
                        LocalizationManager.Apply(settings.Language);
                        return null;
                    },
                    applyTheme: ThemeManager.Apply);
                mainWindow = new MainWindow(settingsViewModel);
                Assert.NotNull(mainWindow.FindName("SettingsContentRoot"));
                var settingsWindowTitle = Assert.IsType<TextBlock>(mainWindow.FindName("SettingsWindowTitle"));
                var settingsNavigation = Assert.IsType<TabControl>(mainWindow.FindName("SettingsNavigation"));
                Assert.Equal(9, settingsNavigation.Items.Count);
                foreach (var item in settingsNavigation.Items.Cast<TabItem>())
                {
                    item.ApplyTemplate();
                    var headerPresenter = Assert.IsType<ContentPresenter>(item.Template.FindName("HeaderPresenter", item));
                    Assert.Equal(item.Header, headerPresenter.Content);
                    Assert.Equal(HorizontalAlignment.Stretch, item.HorizontalContentAlignment);
                }
                mainWindow.Show();
                Volatile.Write(ref stage, "settings window shown");
                mainWindow.Width = 760;
                mainWindow.Height = 560;
                mainWindow.UpdateLayout();
                var selectedPage = Assert.IsAssignableFrom<FrameworkElement>(
                    Assert.IsType<TabItem>(settingsNavigation.SelectedItem).Content);
                Assert.True(selectedPage.ActualWidth > 530, $"Settings page did not stretch: {selectedPage.ActualWidth:F1}px");
                var generalCard = Assert.IsType<Border>(mainWindow.FindName("GeneralIndexingCard"));
                Assert.True(generalCard.ActualWidth > 490, $"Settings card did not fill the narrow viewport: {generalCard.ActualWidth:F1}px");

                var languageSelector = Assert.IsType<ComboBox>(mainWindow.FindName("UiLanguageSelector"));
                Volatile.Write(ref stage, "selecting Chinese language");
                languageSelector.SelectedItem = AppLanguage.SimplifiedChinese;
                Volatile.Write(ref stage, "Chinese selection changed");
                languageSelector.GetBindingExpression(ComboBox.SelectedItemProperty)?.UpdateSource();
                Volatile.Write(ref stage, "Chinese binding updated");
                mainWindow.UpdateLayout();
                Assert.Equal(AppLanguage.SimplifiedChinese, settingsViewModel.SelectedLanguage);
                Assert.Equal("ListaryOpen", mainWindow.Title);
                Assert.Equal("ListaryOpen Options", settingsWindowTitle.Text);
                var generalTab = Assert.IsType<TabItem>(settingsNavigation.Items[0]);
                var generalHeaderPresenter = Assert.IsType<ContentPresenter>(
                    generalTab.Template.FindName("HeaderPresenter", generalTab));
                Assert.Equal("General", generalTab.Header);
                Assert.Equal("General", generalHeaderPresenter.Content);
                Assert.Equal("Save language", Assert.IsType<Button>(mainWindow.FindName("SaveLanguageButton")).Content);

                settingsViewModel.SavePreferencesCommand.Execute(null);
                mainWindow.UpdateLayout();
                Assert.Equal("ListaryOpen", mainWindow.Title);
                Assert.Equal("ListaryOpen 选项", settingsWindowTitle.Text);
                Assert.Equal("常规", generalTab.Header);
                Assert.Equal("常规", generalHeaderPresenter.Content);
                Assert.Equal("保存语言设置", Assert.IsType<Button>(mainWindow.FindName("SaveLanguageButton")).Content);
                Volatile.Write(ref stage, "Chinese language applied");

                languageSelector.SelectedItem = AppLanguage.English;
                languageSelector.GetBindingExpression(ComboBox.SelectedItemProperty)?.UpdateSource();
                mainWindow.UpdateLayout();
                Assert.Equal(AppLanguage.English, settingsViewModel.SelectedLanguage);
                Assert.Equal("ListaryOpen", mainWindow.Title);
                Assert.Equal("ListaryOpen 选项", settingsWindowTitle.Text);
                Assert.Equal("常规", generalTab.Header);
                Assert.Equal("常规", generalHeaderPresenter.Content);
                Assert.Equal("保存语言设置", Assert.IsType<Button>(mainWindow.FindName("SaveLanguageButton")).Content);

                settingsViewModel.SavePreferencesCommand.Execute(null);
                mainWindow.UpdateLayout();
                Assert.Equal("ListaryOpen", mainWindow.Title);
                Assert.Equal("ListaryOpen Options", settingsWindowTitle.Text);
                Assert.Equal("General", generalTab.Header);
                Assert.Equal("General", generalHeaderPresenter.Content);
                Assert.Equal("Save language", Assert.IsType<Button>(mainWindow.FindName("SaveLanguageButton")).Content);
                Volatile.Write(ref stage, "English language restored");

                foreach (var item in settingsNavigation.Items.Cast<TabItem>())
                {
                    settingsNavigation.SelectedItem = item;
                    mainWindow.UpdateLayout();
                    var page = Assert.IsAssignableFrom<FrameworkElement>(item.Content);
                    Assert.True(page.ActualWidth > 490, $"Settings page '{item.Header}' collapsed to {page.ActualWidth:F1}px");
                }

                settingsNavigation.SelectedIndex = 1;
                mainWindow.UpdateLayout();
                var lightBackground = Assert.IsType<System.Windows.Media.SolidColorBrush>(
                    application.FindResource("Brush.AppBackground")).Color;
                settingsViewModel.SelectedTheme = AppTheme.Dark;
                mainWindow.UpdateLayout();
                var darkBackground = Assert.IsType<System.Windows.Media.SolidColorBrush>(
                    application.FindResource("Brush.AppBackground")).Color;
                Assert.NotEqual(lightBackground, darkBackground);

                settingsViewModel.SavePreferencesCommand.Execute(null);
                mainWindow.UpdateLayout();
                var themeSelector = Assert.IsType<ComboBox>(mainWindow.FindName("ThemeSelector"));
                themeSelector.ApplyTemplate();
                var selectedThemePresenter = Assert.IsType<ContentPresenter>(
                    themeSelector.Template.FindName("SelectedItemPresenter", themeSelector));
                Assert.Equal(VerticalAlignment.Center, selectedThemePresenter.VerticalAlignment);
                var darkSurface = Assert.IsType<System.Windows.Media.SolidColorBrush>(application.FindResource("Brush.Surface")).Color;
                Assert.Equal(darkSurface, Assert.IsType<System.Windows.Media.SolidColorBrush>(themeSelector.Background).Color);
                var hotkeyBox = Assert.IsType<TextBox>(mainWindow.FindName("SearchHotkeyBox"));
                Assert.Equal(VerticalAlignment.Center, hotkeyBox.VerticalContentAlignment);

                var darkSuccess = Assert.IsType<System.Windows.Media.SolidColorBrush>(application.FindResource("Brush.Success")).Color;
                ThemeManager.Apply(AppTheme.Geek);
                var geekSuccess = Assert.IsType<System.Windows.Media.SolidColorBrush>(application.FindResource("Brush.Success")).Color;
                Assert.NotEqual(darkSuccess, geekSuccess);
                ThemeManager.Apply(AppTheme.Light);

                Assert.True(mainWindow.UseLayoutRounding);
                Assert.True(mainWindow.SnapsToDevicePixels);
                Assert.Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(mainWindow));

                var alignmentCheckBox = new CheckBox { Content = "Aligned setting", IsChecked = true };
                var previewPane = new FilePreviewPane();
                var layoutProbe = new Grid();
                layoutProbe.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
                layoutProbe.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                layoutProbe.Children.Add(alignmentCheckBox);
                Grid.SetRow(previewPane, 1);
                layoutProbe.Children.Add(previewPane);
                layoutProbeWindow = new Window
                {
                    Width = 420,
                    Height = 390,
                    Content = layoutProbe,
                    ShowInTaskbar = false
                };
                layoutProbeWindow.Show();
                layoutProbeWindow.UpdateLayout();

                alignmentCheckBox.ApplyTemplate();
                var checkGlyphBox = Assert.IsType<Border>(
                    alignmentCheckBox.Template.FindName("CheckGlyphBox", alignmentCheckBox));
                var checkContentPresenter = Assert.IsType<ContentPresenter>(
                    alignmentCheckBox.Template.FindName("CheckContentPresenter", alignmentCheckBox));
                var glyphCenterY = checkGlyphBox.TranslatePoint(
                    new Point(0, checkGlyphBox.ActualHeight / 2),
                    alignmentCheckBox).Y;
                var contentCenterY = checkContentPresenter.TranslatePoint(
                    new Point(0, checkContentPresenter.ActualHeight / 2),
                    alignmentCheckBox).Y;
                Assert.InRange(Math.Abs(glyphCenterY - contentCenterY), 0, 0.5);

                var previewSurface = Assert.IsType<Grid>(previewPane.FindName("ImagePreviewSurface"));
                var previewImage = Assert.IsType<Image>(previewPane.FindName("PreviewImage"));
                var emptyPreview = Assert.IsType<StackPanel>(previewPane.FindName("EmptyPreview"));
                var stride = 1600 * 4;
                previewImage.Source = BitmapSource.Create(
                    1600,
                    900,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null,
                    new byte[stride * 900],
                    stride);
                previewSurface.Visibility = Visibility.Visible;
                emptyPreview.Visibility = Visibility.Collapsed;
                layoutProbeWindow.UpdateLayout();
                Assert.True(previewImage.ActualWidth > 0);
                Assert.True(previewImage.ActualHeight > 0);
                Assert.InRange(previewImage.ActualWidth, 0, previewSurface.ActualWidth + 0.5);
                Assert.InRange(previewImage.ActualHeight, 0, previewSurface.ActualHeight + 0.5);
                Assert.InRange(
                    Math.Abs((previewImage.ActualWidth / previewImage.ActualHeight) - (16d / 9d)),
                    0,
                    0.02);

                layoutProbeWindow.Close();
                layoutProbeWindow = null;

                var selectionService = new RecordingExplorerSelectionService();
                var searchViewModel = new SearchPanelViewModel(new EmptySearchIndex());
                searchPanel = new SearchPanel(searchViewModel, selectionService);
                Volatile.Write(ref stage, "search panel created");
                var queryBox = Assert.IsType<TextBox>(searchPanel.FindName("QueryBox"));
                Assert.NotNull(searchPanel.FindName("ResultsList"));

                var explorerWindow = new IntPtr(42);
                searchPanel.ActivateExplorerSearch("o", Environment.CurrentDirectory, explorerWindow);
                Assert.False(searchPanel.ShowInTaskbar);
                Assert.False(searchPanel.ShowActivated);
                Assert.False(queryBox.IsKeyboardFocusWithin);
                Assert.Equal("o", queryBox.Text);
                Assert.Equal(queryBox.Text.Length, queryBox.CaretIndex);
                Assert.Equal(0, queryBox.SelectionLength);

                searchPanel.AppendExplorerSearchText("p");
                Assert.False(queryBox.IsKeyboardFocusWithin);
                Assert.Equal("op", queryBox.Text);
                Assert.Equal(queryBox.Text.Length, queryBox.CaretIndex);
                Assert.Equal(0, queryBox.SelectionLength);

                searchPanel.ExecuteExplorerSearchEditCommand(GlobalEditCommand.Backspace);
                Assert.Equal("o", queryBox.Text);
                Assert.Equal("o", searchViewModel.QueryText);

                queryBox.SelectAll();
                searchPanel.ExecuteExplorerSearchEditCommand(GlobalEditCommand.Backspace);
                Assert.Equal(string.Empty, queryBox.Text);
                Assert.Equal(string.Empty, searchViewModel.QueryText);

                searchPanel.DismissExplorerSearch();
                Assert.False(searchPanel.IsExplorerTypeSearchActive);
                searchPanel.ActivateExplorerSearch("second", Environment.CurrentDirectory, explorerWindow);
                Assert.True(searchPanel.IsExplorerTypeSearchActive);
                Assert.Equal("second", queryBox.Text);
                Assert.Equal(queryBox.Text.Length, queryBox.CaretIndex);

                var firstPath = Path.Combine(Environment.CurrentDirectory, "First.txt");
                var secondPath = Path.Combine(Environment.CurrentDirectory, "Second.txt");
                var firstResult = new SearchResult(
                    FileRecord.Create(firstPath, false, 1, DateTimeOffset.UtcNow),
                    100,
                    "test");
                var secondResult = new SearchResult(
                    FileRecord.Create(secondPath, false, 1, DateTimeOffset.UtcNow),
                    90,
                    "test");
                searchViewModel.Results.ReplaceAll([firstResult, secondResult]);
                searchViewModel.SelectedResult = firstResult;
                Assert.Equal(explorerWindow, selectionService.ExplorerWindow);
                Assert.Equal(Environment.CurrentDirectory, selectionService.CurrentFolder);
                Assert.Equal(firstPath, selectionService.ItemPath);

                var cancellationsBeforeNoSelection = selectionService.CancelPendingCount;
                searchViewModel.SelectedResult = null;
                Assert.Equal(cancellationsBeforeNoSelection + 1, selectionService.CancelPendingCount);
                searchViewModel.SelectedResult = firstResult;

                var stressResults = Enumerable.Range(0, 50)
                    .Select(index => new SearchResult(
                        FileRecord.Create(
                            Path.Combine(Environment.CurrentDirectory, $"stress-{index}.txt"),
                            false,
                            index,
                            DateTimeOffset.UtcNow),
                        100 - index,
                        "name-substring"))
                    .ToArray();
                for (var frame = 0; frame < 12; frame++)
                {
                    searchViewModel.SetResultsForTesting(frame % 2 == 0 ? stressResults : stressResults[..1]);
                    searchPanel.Width = frame % 2 == 0 ? 1080 : 760;
                    searchPanel.UpdateLayout();
                }
                Assert.Single(searchViewModel.Results);

                searchViewModel.SetResultsForTesting([firstResult, secondResult]);
                searchViewModel.SelectedResult = firstResult;

                var resultsList = Assert.IsType<ListBox>(searchPanel.FindName("ResultsList"));
                Assert.NotNull(resultsList.ContextMenu);
                resultsList.ContextMenu.PlacementTarget = resultsList;
                resultsList.ContextMenu.IsOpen = true;
                Assert.True(searchPanel.IsResultContextMenuOpen);
                searchPanel.CloseResultContextMenu();
                Assert.False(resultsList.ContextMenu.IsOpen);
                Assert.False(searchPanel.IsResultContextMenuOpen);

                searchPanel.MoveSearchSelection(1);
                Assert.Equal(secondResult, searchViewModel.SelectedResult);
                Assert.Equal(secondPath, selectionService.ItemPath);

                searchPanel.ActivateSearch();
                Assert.False(searchPanel.ShowInTaskbar);
                Assert.Equal(string.Empty, queryBox.Text);
                var workArea = SystemParameters.WorkArea;
                Assert.InRange(searchPanel.Left, workArea.Left, workArea.Right - searchPanel.Width);
                Assert.InRange(searchPanel.Top, workArea.Top, workArea.Bottom - searchPanel.Height);

                var activatedAnchorWindows = new List<IntPtr>();
                quickSwitchBar = new QuickSwitchBarWindow(
                    (SearchPanelViewModel)searchPanel.DataContext,
                    windowHandle =>
                    {
                        activatedAnchorWindows.Add(windowHandle);
                        return true;
                    });
                Volatile.Write(ref stage, "quick switch created");
                Assert.NotNull(quickSwitchBar.FindName("QuickSwitchQueryBox"));
                Assert.NotNull(quickSwitchBar.FindName("QuickSwitchResults"));

                var dialogWindow = new IntPtr(42);
#pragma warning disable xUnit1031 // The UI smoke test owns this dedicated STA thread and does not run under xUnit's synchronization context.
                quickSwitchBar.AttachAsync(
                        [new QuickSwitchFolderCandidate(
                            Environment.CurrentDirectory,
                            "Explorer",
                            new IntPtr(84),
                            true)],
                        (_, _) => Task.FromResult(new DialogJumpResult(
                            DialogJumpStatus.Success,
                            "Dialog folder changed.")),
                        dialogWindow)
                    .GetAwaiter()
                    .GetResult();
#pragma warning restore xUnit1031
                Assert.True(quickSwitchBar.IsAttached);
                Assert.True(searchViewModel.IsQuickSwitchBarCollapsed);
                quickSwitchBar.PrepareForFollowUpTyping();
                Assert.True(quickSwitchBar.IsFollowUpTypingArmed);
                Assert.True(quickSwitchBar.IsDialogTextInputCaptureActive);
                quickSwitchBar.AttachAsync(
                        [new QuickSwitchFolderCandidate(
                            Environment.CurrentDirectory,
                            "Explorer",
                            new IntPtr(42),
                            true)],
                        (_, _) => Task.FromResult(new DialogJumpResult(
                            DialogJumpStatus.Success,
                            "Jumped.")),
                        new IntPtr(42))
                    .GetAwaiter()
                    .GetResult();
                Assert.True(
                    quickSwitchBar.IsFollowUpTypingArmed,
                    "Refreshing the same attached dialog must not consume the Ctrl+G follow-up key state.");
                Assert.True(quickSwitchBar.TryAppendDialogText("f", dialogWindow));
                Assert.False(quickSwitchBar.IsFollowUpTypingArmed);
                Assert.Equal("f", searchViewModel.QueryText);
                searchViewModel.ResetQuickSwitchQuery();
                quickSwitchBar.Collapse();
                Assert.False(quickSwitchBar.TryAppendDialogText("x", new IntPtr(41)));
                Assert.NotEmpty(searchViewModel.Results);
                searchViewModel.SelectedResult = searchViewModel.Results[0];
                quickSwitchBar.Expand();
#pragma warning disable xUnit1031 // The activation delegate completes synchronously on this dedicated STA thread.
                quickSwitchBar.ActivateSelectedAsync().GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                Assert.True(searchViewModel.IsQuickSwitchBarCollapsed);
                Assert.True(
                    quickSwitchBar.IsFollowUpTypingArmed,
                    "Panel selection jump must arm follow-up typing like Ctrl+G.");
                Assert.Equal(dialogWindow, Assert.Single(activatedAnchorWindows));
                var captureAfterJump = WpfApp.ShouldCaptureOverlayInput(
                    quickSwitchBar.IsDialogTextInputCaptureActive,
                    explorerQuickMenuOpen: false,
                    taskManagerSearchActive: false,
                    resultContextMenuOpen: false,
                    explorerTypeSearchActive: false);
                Assert.True(
                    captureAfterJump,
                    "Follow-up typing after a dialog jump must keep overlay text capture armed.");
                Assert.True(quickSwitchBar.TryAppendDialogText("o", dialogWindow));
                Assert.False(searchViewModel.IsQuickSwitchBarCollapsed);
                Assert.True(quickSwitchBar.IsDialogSearchExpanded);
                Assert.Equal("o", searchViewModel.QueryText);
                Assert.NotEqual(IntPtr.Zero, quickSwitchBar.WindowHandle);
                quickSwitchBar.CollapseFromDialogInput(quickSwitchBar.WindowHandle);
                Assert.True(searchViewModel.IsQuickSwitchBarCollapsed);
                Assert.Equal(new[] { dialogWindow, dialogWindow }, activatedAnchorWindows);
                searchViewModel.ResetQuickSwitchQuery();
                Assert.True(quickSwitchBar.TryAppendDialogText("o", dialogWindow));
                quickSwitchBar.ExecuteEditCommandFromDialogInput(GlobalEditCommand.Backspace, dialogWindow);
                Assert.Equal(string.Empty, searchViewModel.QueryText);
                searchViewModel.ResetQuickSwitchQuery();
                quickSwitchBar.Collapse();
                Assert.False(quickSwitchBar.IsDialogSearchExpanded);
                Assert.True(quickSwitchBar.TryAppendDialogText("n", dialogWindow));
                Assert.Equal("n", searchViewModel.QueryText);
                quickSwitchBar.ExecuteEditCommandFromDialogInput(GlobalEditCommand.SelectAll, dialogWindow);
                var quickSwitchQueryBox = (TextBox)quickSwitchBar.FindName("QuickSwitchQueryBox");
                Assert.Equal(1, quickSwitchQueryBox.SelectionLength);
                Assert.True(quickSwitchBar.TryAppendDialogText("replacement", dialogWindow));
                Assert.Equal("replacement", searchViewModel.QueryText);
                Assert.True(quickSwitchBar.TryAppendDialogText(" ", dialogWindow));
                Assert.Equal("replacement ", searchViewModel.QueryText);
                quickSwitchBar.ExecuteEditCommandFromDialogInput(GlobalEditCommand.SelectAll, dialogWindow);
                quickSwitchBar.ExecuteEditCommandFromDialogInput(GlobalEditCommand.Backspace, dialogWindow);
                Assert.Equal(string.Empty, searchViewModel.QueryText);
                quickSwitchBar.Collapse();
                Assert.True(searchViewModel.IsQuickSwitchBarCollapsed);
                quickSwitchQueryBox.Text = "second search";
                quickSwitchQueryBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                Assert.False(searchViewModel.IsQuickSwitchBarCollapsed);
                Assert.Equal("second search", searchViewModel.QueryText);

                var taskManagerAutomation = new RecordingTaskManagerAutomationService(
                [
                    new TaskManagerItem("firefox", "Firefox", "firefox.exe"),
                    new TaskManagerItem("firebird", "Firebird", "firebird.exe"),
                    new TaskManagerItem("onepassword", "1Password", "1Password.exe")
                ]);
                taskManagerSearchWindow = new TaskManagerSearchWindow(taskManagerAutomation);
                taskManagerSearchWindow.ActivateSearch("fire", new IntPtr(126));
                var taskQueryBox = Assert.IsType<TextBox>(taskManagerSearchWindow.FindName("TaskQueryBox"));
                var taskSearchViewModel = Assert.IsType<TaskManagerSearchViewModel>(taskManagerSearchWindow.DataContext);
                Assert.False(taskManagerSearchWindow.ShowActivated);
                Assert.False(taskQueryBox.IsKeyboardFocusWithin);
                Assert.True(taskManagerSearchWindow.IsTaskManagerSearchActive);
                Assert.Equal("fire", taskQueryBox.Text);
                Assert.Equal(taskQueryBox.Text.Length, taskQueryBox.CaretIndex);
                Assert.True(
                    WaitForDispatcherCondition(
                        () => taskSearchViewModel.Results.Count >= 2 && taskSearchViewModel.SelectedItem is not null,
                        TimeSpan.FromSeconds(3)),
                    "Task Manager search results did not become ready before selection was tested.");
                taskManagerSearchWindow.MoveSelection(1);
                Assert.NotNull(taskSearchViewModel.SelectedItem);
                Assert.Null(taskManagerAutomation.SelectedItem);
                Assert.False(taskManagerAutomation.ActivateSelection);
                taskManagerSearchWindow.ExecuteEditCommand(GlobalEditCommand.SelectAll);
                Assert.Equal(taskQueryBox.Text.Length, taskQueryBox.SelectionLength);
                taskManagerSearchWindow.AppendText("1pass");
                Assert.Equal("1pass", taskQueryBox.Text);
                Assert.False(taskQueryBox.IsKeyboardFocusWithin);
                taskManagerSearchWindow.ExecuteEditCommand(GlobalEditCommand.Backspace);
                Assert.Equal("1pas", taskQueryBox.Text);
                taskManagerSearchWindow.AppendText("😀");
                Assert.Equal("1pas😀", taskQueryBox.Text);
                taskManagerSearchWindow.ExecuteEditCommand(GlobalEditCommand.Backspace);
                Assert.Equal("1pas", taskQueryBox.Text);
                taskManagerSearchWindow.DismissSearch();
                Assert.False(taskManagerSearchWindow.IsTaskManagerSearchActive);
                Assert.True(taskManagerAutomation.CancelPendingCount > 0);
                taskManagerSearchWindow.ActivateSearch("firefox", new IntPtr(126));
                taskManagerSearchWindow.ActivateResultShortcut(0);
                Assert.False(taskManagerSearchWindow.IsTaskManagerSearchActive);
                Assert.NotNull(taskManagerAutomation.SelectedItem);
                Assert.Equal("Firefox", taskManagerAutomation.SelectedItem!.Name);
                Assert.True(taskManagerAutomation.ActivateSelection);

                var retryingTaskManagerAutomation = new SequencedTaskManagerAutomationService(
                [
                    [],
                    [
                        new TaskManagerItem("firefox-retry", "Firefox", "firefox.exe"),
                        new TaskManagerItem("firebird-retry", "Firebird", "firebird.exe")
                    ]
                ]);
                taskManagerRetryWindow = new TaskManagerSearchWindow(retryingTaskManagerAutomation);
                taskManagerRetryWindow.ActivateSearch("fire", new IntPtr(127));
                var retryViewModel = Assert.IsType<TaskManagerSearchViewModel>(taskManagerRetryWindow.DataContext);
                Assert.True(
                    WaitForDispatcherCondition(
                        () => retryViewModel.Results.Count == 2 && retryViewModel.SelectedItem is not null,
                        TimeSpan.FromSeconds(3)),
                    "Task Manager search did not recover from a transient empty WinUI row snapshot.");
                Assert.Equal(2, retryingTaskManagerAutomation.GetItemsCallCount);

                var quickMenu = new ExplorerQuickMenu(() => { }, _ => { });
                quickMenu.Show(
                    Environment.CurrentDirectory,
                    [new QuickSwitchFolderCandidate(
                        Environment.CurrentDirectory,
                        "Explorer",
                        new IntPtr(42),
                        true)],
                    AppSettings.Defaults().QuickMenuEntries,
                    100,
                    100,
                    IntPtr.Zero);
                Assert.True(quickMenu.IsOpen);
                quickMenu.CloseOpenMenu();
                Assert.False(quickMenu.IsOpen);
                Volatile.Write(ref stage, "UI checks completed");
            }
            catch (Exception caught)
            {
                exception = caught;
            }
            finally
            {
                Volatile.Write(ref stage, "cleaning up");
                // This smoke test constructs an Application without running its
                // dispatcher loop. Mark the dispatcher as shutting down first so
                // the production close-to-tray handlers allow each HWND to close.
                application?.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                layoutProbeWindow?.Close();
                taskManagerRetryWindow?.Close();
                taskManagerSearchWindow?.Close();
                quickSwitchBar?.Close();
                searchPanel?.Close();
                mainWindow?.Close();
                application?.Shutdown();
                // Pump the queued dispatcher shutdown before the STA thread exits;
                // otherwise WPF can retain an HwndSubclass and crash the test host
                // when a later test causes its finalizer to run.
                Dispatcher.Run();
                completed = true;
                Volatile.Write(ref stage, "completed");
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        var finishedInTime = thread.Join(TimeSpan.FromSeconds(10));

        Assert.True(finishedInTime, $"WPF smoke test STA thread did not finish within 10 seconds (stage: {Volatile.Read(ref stage)}).");
        Assert.True(completed, "WPF smoke test STA thread did not complete.");
        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    private static bool WaitForDispatcherCondition(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(10)
            };
            timer.Tick += (_, _) => frame.Continue = false;
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }

        return condition();
    }

    private sealed class RecordingExplorerSelectionService : IExplorerSelectionService
    {
        public IntPtr ExplorerWindow { get; private set; }

        public string? CurrentFolder { get; private set; }

        public string? ItemPath { get; private set; }

        public int CancelPendingCount { get; private set; }

        public bool TrySelectItem(IntPtr explorerWindow, string currentFolder, string itemPath)
        {
            ExplorerWindow = explorerWindow;
            CurrentFolder = currentFolder;
            ItemPath = itemPath;
            return true;
        }

        public void CancelPending() => CancelPendingCount++;
    }

    private sealed class RecordingTaskManagerAutomationService(
        IReadOnlyList<TaskManagerItem> items) : ITaskManagerAutomationService
    {
        public int CancelPendingCount { get; private set; }

        public TaskManagerItem? SelectedItem { get; private set; }

        public bool ActivateSelection { get; private set; }

        public Task<IReadOnlyList<TaskManagerItem>> GetItemsAsync(
            IntPtr taskManagerWindow,
            CancellationToken cancellationToken) =>
            Task.FromResult(items);

        public void QueueSelectItem(IntPtr taskManagerWindow, TaskManagerItem item, bool activate = false)
        {
            SelectedItem = item;
            ActivateSelection = activate;
        }

        public void CancelPending() => CancelPendingCount++;

        public void Dispose()
        {
        }
    }

    private sealed class SequencedTaskManagerAutomationService(
        IReadOnlyList<IReadOnlyList<TaskManagerItem>> snapshots) : ITaskManagerAutomationService
    {
        private int _nextSnapshot;

        public int GetItemsCallCount { get; private set; }

        public Task<IReadOnlyList<TaskManagerItem>> GetItemsAsync(
            IntPtr taskManagerWindow,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetItemsCallCount++;
            var index = Math.Min(_nextSnapshot++, snapshots.Count - 1);
            return Task.FromResult(snapshots[index]);
        }

        public void QueueSelectItem(IntPtr taskManagerWindow, TaskManagerItem item, bool activate = false)
        {
        }

        public void CancelPending()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class EmptySearchIndex : ISearchIndex
    {
        public Task UpsertAsync(FileRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(string fullPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordUsageAsync(string fullPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            SearchQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
    }

}
