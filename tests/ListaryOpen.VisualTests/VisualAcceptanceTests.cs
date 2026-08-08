using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ListaryOpen.App;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Core.Settings;
using ListaryOpen.Infrastructure.Hooks;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.VisualTests;

public sealed class VisualAcceptanceTests
{
    [Fact]
    [Trait("Category", "VisualAcceptance")]
    public void ProductionWindowsRenderVisualAcceptanceMatrix()
    {
        Exception? failure = null;
        var completed = false;
        var stage = "not started";
        var thread = new Thread(() =>
        {
            Application? application = null;
            Window? desktopBackdrop = null;
            MainWindow? settingsWindow = null;
            SearchPanel? searchPanel = null;
            QuickSwitchBarWindow? quickSwitch = null;
            Window? quickSwitchBackdrop = null;
            TaskManagerSearchWindow? taskManager = null;
            var evidence = new List<VisualEvidence>();
            var screenshotDirectory = GetScreenshotDirectory();
            var fixtureDirectory = Path.Combine(screenshotDirectory, ".fixture");

            try
            {
                stage = "initialize production resources";
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                Directory.CreateDirectory(screenshotDirectory);
                Directory.CreateDirectory(fixtureDirectory);
                foreach (var stale in Directory.EnumerateFiles(screenshotDirectory, "*.png"))
                {
                    File.Delete(stale);
                }

                application = CreateVisualTestApplication();
                ThemeManager.Apply(AppTheme.Light);
                LocalizationManager.Apply(AppLanguage.English);
                desktopBackdrop = CreateDesktopEvidenceBackdrop();
                desktopBackdrop.Show();

                var settings = AppSettings.Defaults().WithPreferences(
                    AppSettings.Defaults().IndexedRoots,
                    ["node_modules", "*.tmp", Path.Combine(Path.GetTempPath(), "cache")],
                    AppTheme.Light,
                    IndexUpdateFrequency.Hourly,
                    AppSettings.CreateDefaultQuickMenuEntries(),
                    checkForUpdates: true,
                    quickLaunchEntries:
                    [
                        new QuickLaunchEntry("terminal", "term", "Windows Terminal", "wt.exe"),
                        new QuickLaunchEntry("notes", "notes", "Open notes", "notepad.exe")
                    ],
                    language: AppLanguage.English);
                var settingsViewModel = new SettingsViewModel(
                    settings,
                    enableNtfsFastIndexing: null,
                    enableHookQuickSwitch: null,
                    applySettings: value =>
                    {
                        LocalizationManager.Apply(value.Language);
                        return null;
                    },
                    applyTheme: ThemeManager.Apply,
                    ntfsFastIndexingEnabled: true);
                settingsViewModel.UpdateHookQuickSwitchStatus(new HookQuickSwitchStatus(
                    true,
                    new HookArchitectureStatus(HookArchitecture.X64, true, true, true, "x64 ready"),
                    new HookArchitectureStatus(HookArchitecture.X86, true, true, true, "x86 ready")));
                settingsWindow = new MainWindow(settingsViewModel)
                {
                    Width = 980,
                    Height = 700,
                    Left = 40,
                    Top = 40
                };
                settingsWindow.Show();
                var navigation = Assert.IsType<TabControl>(settingsWindow.FindName("SettingsNavigation"));
                var themeSelector = Assert.IsType<ComboBox>(settingsWindow.FindName("ThemeSelector"));
                var languageSelector = Assert.IsType<ComboBox>(settingsWindow.FindName("UiLanguageSelector"));
                Assert.Equal(AppTheme.Light, settingsViewModel.SelectedTheme);
                Assert.Equal(AppTheme.Light, themeSelector.SelectedItem);
                Assert.Equal(AppLanguage.English, settingsViewModel.SelectedLanguage);
                Assert.Equal(AppLanguage.English, languageSelector.SelectedItem);
                Assert.Equal(9, navigation.Items.Count);
                var pageNames = new[]
                {
                    "general", "appearance", "hotkeys", "file-search", "language",
                    "actions", "quick-launch", "menu", "about"
                };
                for (var index = 0; index < pageNames.Length; index++)
                {
                    stage = $"settings {pageNames[index]} light English";
                    navigation.SelectedIndex = index;
                    stage = $"settings {pageNames[index]} pump";
                    PumpLayout(settingsWindow);
                    Assert.Equal(index, navigation.SelectedIndex);
                    stage = $"settings {pageNames[index]} capture";
                    Capture(settingsWindow, screenshotDirectory, $"{index + 1:00}-settings-{pageNames[index]}-light-en", stage, evidence);
                    stage = $"settings {pageNames[index]} captured";
                }

                stage = "settings appearance dark English";
                navigation.SelectedIndex = 1;
                settingsViewModel.SelectedTheme = AppTheme.Dark;
                PumpLayout(settingsWindow);
                Assert.Equal(1, navigation.SelectedIndex);
                Assert.Equal(AppTheme.Dark, themeSelector.SelectedItem);
                Assert.Equal(AppTheme.Dark, ThemeManager.Current);
                Capture(settingsWindow, screenshotDirectory, "10-settings-appearance-dark-en", stage, evidence);

                stage = "settings appearance geek English";
                settingsViewModel.SelectedTheme = AppTheme.Geek;
                PumpLayout(settingsWindow);
                Assert.Equal(1, navigation.SelectedIndex);
                Assert.Equal(AppTheme.Geek, themeSelector.SelectedItem);
                Assert.Equal(AppTheme.Geek, ThemeManager.Current);
                Capture(settingsWindow, screenshotDirectory, "11-settings-appearance-geek-en", stage, evidence);

                stage = "settings language light Chinese";
                settingsViewModel.SelectedTheme = AppTheme.Light;
                settingsViewModel.SelectedLanguage = AppLanguage.SimplifiedChinese;
                Assert.True(settingsViewModel.SavePreferencesCommand.CanExecute(null));
                settingsViewModel.SavePreferencesCommand.Execute(null);
                navigation.SelectedIndex = 4;
                PumpLayout(settingsWindow);
                Assert.Equal(4, navigation.SelectedIndex);
                Assert.Equal(AppTheme.Light, themeSelector.SelectedItem);
                Assert.Equal(AppTheme.Light, ThemeManager.Current);
                Assert.Equal(AppLanguage.SimplifiedChinese, languageSelector.SelectedItem);
                Assert.Equal(AppLanguage.SimplifiedChinese, LocalizationManager.EffectiveLanguage);
                Assert.Equal("ListaryOpen", settingsWindow.Title);
                Assert.Equal(
                    "ListaryOpen 选项",
                    Assert.IsType<TextBlock>(settingsWindow.FindName("SettingsWindowTitle")).Text);
                Assert.Equal("设置已保存并应用。", settingsViewModel.SettingsStatusText);
                Capture(settingsWindow, screenshotDirectory, "12-settings-language-light-zh", stage, evidence);

                stage = "settings actions light Chinese semantic badges";
                navigation.SelectedIndex = 5;
                PumpLayout(settingsWindow);
                Assert.Equal(5, navigation.SelectedIndex);
                Assert.Equal("已启用", settingsViewModel.NtfsFastIndexingBadgeText);
                Assert.Equal("就绪", settingsViewModel.HookQuickSwitchBadgeText);
                Assert.Equal("Enabled", settingsViewModel.NtfsFastIndexingBadgeKind);
                Assert.Equal("Ready", settingsViewModel.HookQuickSwitchBadgeKind);
                Capture(settingsWindow, screenshotDirectory, "19-settings-actions-light-zh", stage, evidence);
                settingsWindow.Hide();

                LocalizationManager.Apply(AppLanguage.English);
                var previewPath = Path.Combine(fixtureDirectory, "Visual acceptance preview.txt");
                var currentProjectPath = Directory.CreateDirectory(
                    Path.Combine(fixtureDirectory, "Current Project")).FullName;
                File.WriteAllText(
                    previewPath,
                    "ListaryOpen visual acceptance\r\n\r\nThe preview should remain readable and aligned.");
                var now = DateTimeOffset.Now;
                var searchResults = new[]
                {
                    Result(Path.Combine(fixtureDirectory, "Projects"), true, 0, now, 100, "exact-name"),
                    Result(previewPath, false, new FileInfo(previewPath).Length, now, 96, "name-prefix"),
                    Result(Path.Combine(fixtureDirectory, "Design mockup.png"), false, 483_220, now.AddDays(-1), 88, "pinyin"),
                    Result(Path.Combine(fixtureDirectory, "Release checklist.md"), false, 8_192, now.AddDays(-2), 81, "name-substring")
                };
                var searchViewModel = new SearchPanelViewModel(new StaticSearchIndex(searchResults));
                searchPanel = new SearchPanel(searchViewModel);
                ThemeManager.Apply(AppTheme.Light);

                stage = "global search empty query recommendations";
                searchPanel.ActivateSearch();
                PumpLayout(searchPanel);
                Assert.Equal(string.Empty, searchViewModel.QueryText);
                Assert.True(searchViewModel.AreResultsVisible);
                AssertCompactSearchHasNoOuterBackgroundBand(searchPanel);
                Capture(searchPanel, screenshotDirectory, "13-search-empty-light-en", stage, evidence);

                stage = "global search results and preview";
                searchViewModel.QueryText = "visual";
                searchViewModel.SetResultsForTesting(searchResults);
                searchViewModel.SelectedResult = searchResults[1];
                var previewPane = Assert.IsType<FilePreviewPane>(searchPanel.FindName("PreviewPane"));
                var previewText = Assert.IsType<TextBox>(previewPane.FindName("PreviewText"));
                var emptyPreview = Assert.IsType<StackPanel>(previewPane.FindName("EmptyPreview"));
                Assert.True(
                    WaitForDispatcherCondition(
                        () => previewText.Text == File.ReadAllText(previewPath) &&
                            previewText.Visibility == Visibility.Visible &&
                            emptyPreview.Visibility == Visibility.Collapsed,
                        TimeSpan.FromSeconds(3)),
                    "The production result-selection path did not finish loading the text preview.");
                Assert.Equal(File.ReadAllText(previewPath), previewText.Text);
                Assert.Equal(Visibility.Visible, previewText.Visibility);
                Assert.Equal(Visibility.Collapsed, emptyPreview.Visibility);
                PumpLayout(searchPanel);
                Assert.True(searchViewModel.AreResultsVisible);
                Capture(searchPanel, screenshotDirectory, "14-search-results-preview-light-en", stage, evidence);

                stage = "global search results dark Chinese";
                ThemeManager.Apply(AppTheme.Dark);
                LocalizationManager.Apply(AppLanguage.SimplifiedChinese);
                PumpLayout(searchPanel);
                Capture(searchPanel, screenshotDirectory, "15-search-results-dark-zh", stage, evidence);
                searchPanel.Hide();

                LocalizationManager.Apply(AppLanguage.English);
                ThemeManager.Apply(AppTheme.Geek);
                var quickViewModel = new SearchPanelViewModel(new StaticSearchIndex(searchResults));
                quickSwitch = new QuickSwitchBarWindow(quickViewModel, _ => true);
                stage = "quick switch collapsed";
#pragma warning disable xUnit1031 // This dedicated STA owns and pumps the production WPF windows.
                quickSwitch.AttachAsync(
                        [
                            new QuickSwitchFolderCandidate(currentProjectPath, "Explorer", new IntPtr(84), true),
                            new QuickSwitchFolderCandidate(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Explorer", new IntPtr(85), false)
                        ],
                        (_, _) => Task.FromResult(new ListaryOpen.Infrastructure.Dialog.DialogJumpResult(
                            ListaryOpen.Infrastructure.Dialog.DialogJumpStatus.Success,
                            "Dialog folder changed.")),
                        new IntPtr(42))
                    .GetAwaiter()
                    .GetResult();
#pragma warning restore xUnit1031
                quickSwitchBackdrop = CreateTransparentWindowEvidenceBackdrop(quickSwitch);
                quickSwitchBackdrop.Show();
                quickSwitch.Topmost = true;
                quickSwitch.Activate();
                PumpLayout(quickSwitch);
                Capture(quickSwitch, screenshotDirectory, "16-quick-switch-collapsed-geek-en", stage, evidence);

                stage = "quick switch expanded with results";
                Assert.True(quickSwitch.TryAppendDialogText("pro", new IntPtr(42)));
                Assert.True(
                    WaitForDispatcherCondition(
                        () => quickViewModel.Results.Count > 0 &&
                            !string.Equals(quickViewModel.StatusText, "Searching...", StringComparison.Ordinal) &&
                            !quickViewModel.StatusText.StartsWith("Recommended", StringComparison.Ordinal),
                        TimeSpan.FromSeconds(3)),
                    "Quick Switch did not publish the real query results and matching status.");
                Assert.NotEmpty(quickViewModel.Results);
                Assert.All(quickViewModel.Results, result => Assert.True(result.Record.IsDirectory));
                PumpLayout(quickSwitch);
                Capture(quickSwitch, screenshotDirectory, "17-quick-switch-expanded-geek-en", stage, evidence);

                stage = "quick switch collapsed light English";
                ThemeManager.Apply(AppTheme.Light);
                quickViewModel.QueryText = string.Empty;
                quickSwitch.Collapse();
                PumpLayout(quickSwitch);
                Capture(quickSwitch, screenshotDirectory, "21-quick-switch-collapsed-light-en", stage, evidence);

                stage = "quick switch expanded light English";
                Assert.True(quickSwitch.TryAppendDialogText("pro", new IntPtr(42)));
                Assert.True(
                    WaitForDispatcherCondition(
                        () => quickViewModel.Results.Count > 0 &&
                            !string.Equals(quickViewModel.StatusText, "Searching...", StringComparison.Ordinal) &&
                            !quickViewModel.StatusText.StartsWith("Recommended", StringComparison.Ordinal),
                        TimeSpan.FromSeconds(3)),
                    "Quick Switch did not publish the light-theme query results and matching status.");
                PumpLayout(quickSwitch);
                Capture(quickSwitch, screenshotDirectory, "22-quick-switch-expanded-light-en", stage, evidence);
                quickSwitch.Hide();
                quickSwitchBackdrop.Close();
                quickSwitchBackdrop = null;

                ThemeManager.Apply(AppTheme.Dark);
                var applicationIconPath = Path.Combine(AppContext.BaseDirectory, "ListaryOpen.App.exe");
                Assert.True(File.Exists(applicationIconPath));
                var taskManagerAutomation = new StaticTaskManagerAutomationService(
                [
                    new TaskManagerItem("listary-1", "ListaryOpen", "Application process", applicationIconPath),
                    new TaskManagerItem("terminal-1", "Windows Terminal", "3 processes", "wt.exe"),
                    new TaskManagerItem("explorer-1", "Windows Explorer", "File manager", "explorer.exe")
                ]);
                taskManager = new TaskManagerSearchWindow(taskManagerAutomation);
                stage = "task manager search results";
                taskManager.ActivateSearch("i", new IntPtr(126));
                var taskManagerViewModel = Assert.IsType<TaskManagerSearchViewModel>(taskManager.DataContext);
                CompleteWithDispatcher(taskManagerViewModel.WaitForPendingRefreshAsync());
                ThemeManager.Apply(AppTheme.Dark);
                LocalizationManager.Apply(AppLanguage.English);
                Assert.True(taskManagerViewModel.Results.Count >= 2);
                var initialTaskManagerSelection = Assert.IsType<TaskManagerItem>(
                    taskManagerViewModel.SelectedItem);
                PumpLayout(taskManager);
                Assert.True(
                    WaitForDispatcherCondition(
                        () => FindVisualDescendant<Image>(
                            taskManager,
                            image => string.Equals(
                                    FileIcon.GetPath(image),
                                    applicationIconPath,
                                    StringComparison.OrdinalIgnoreCase) &&
                                image.Source is not null &&
                                FileIcon.GetHasIcon(image)) is not null,
                        TimeSpan.FromSeconds(3)),
                    "The Task Manager result did not render the real ListaryOpen executable icon.");
                Capture(taskManager, screenshotDirectory, "18-task-manager-search-dark-en", stage, evidence);

                stage = "task manager next preview preserves host selection";
                taskManager.MoveSelection(1);
                var movedTaskManagerSelection = Assert.IsType<TaskManagerItem>(
                    taskManagerViewModel.SelectedItem);
                Assert.NotEqual(initialTaskManagerSelection.Id, movedTaskManagerSelection.Id);
                Assert.Null(taskManagerAutomation.SelectedItem);
                Assert.False(taskManagerAutomation.ActivateSelection);
                PumpLayout(taskManager);
                Capture(taskManager, screenshotDirectory, "20-task-manager-selection-next-dark-en", stage, evidence);

                AssertEvidenceMatchesExpectedMatrix(evidence);
                var manifestPath = Path.Combine(screenshotDirectory, "visual-evidence.json");
                File.WriteAllText(
                    manifestPath,
                    JsonSerializer.Serialize(
                        new
                        {
                            schemaVersion = 2,
                            expectedMatrix = "tests/visual-acceptance-matrix.json",
                            generatedAtUtc = DateTimeOffset.UtcNow,
                            operatingSystem = Environment.OSVersion.VersionString,
                            culture = System.Globalization.CultureInfo.CurrentCulture.Name,
                            uiCulture = System.Globalization.CultureInfo.CurrentUICulture.Name,
                            screenshots = evidence
                        },
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception exception)
            {
                failure = new InvalidOperationException($"Visual acceptance failed at stage '{stage}'.", exception);
            }
            finally
            {
                application?.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                taskManager?.Close();
                quickSwitch?.Close();
                quickSwitchBackdrop?.Close();
                searchPanel?.Close();
                settingsWindow?.Close();
                desktopBackdrop?.Close();
                application?.Shutdown();
                RestoreMinimizedExternalWindows();
                Dispatcher.Run();
                completed = true;
            }
        })
        {
            IsBackground = true,
            Name = "ListaryOpen visual acceptance STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(40)), $"Visual acceptance timed out at stage '{stage}'.");
        Assert.True(completed, "Visual acceptance thread did not complete.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static Application CreateVisualTestApplication()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var resource in new[]
        {
            "Styles/Colors.xaml",
            "Styles/Typography.xaml",
            "Styles/Controls.xaml",
            "Styles/SearchPanel.xaml",
            "Styles/Settings.xaml"
        })
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/ListaryOpen.App;component/{resource}", UriKind.Absolute)
            });
        }

        application.Resources["ListaryOpenIcon"] = new BitmapImage(
            new Uri("pack://application:,,,/ListaryOpen.App;component/Assets/ListaryOpen.ico", UriKind.Absolute));
        return application;
    }

    private static Window CreateTransparentWindowEvidenceBackdrop(Window target)
    {
        const double padding = 36;
        return new Window
        {
            Width = Math.Max(700, target.Width + (padding * 2)),
            Height = Math.Max(460, target.Height + (padding * 2)),
            Left = target.Left - padding,
            Top = target.Top - padding,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = false,
            Background = new SolidColorBrush(Color.FromRgb(16, 20, 18))
        };
    }

    private static Window CreateDesktopEvidenceBackdrop()
    {
        var area = SystemParameters.WorkArea;
        return new Window
        {
            Width = area.Width,
            Height = area.Height,
            Left = area.Left,
            Top = area.Top,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = false,
            Background = new SolidColorBrush(Color.FromRgb(28, 30, 34))
        };
    }

    private static SearchResult Result(
        string path,
        bool directory,
        long size,
        DateTimeOffset modified,
        double score,
        string reason) =>
        new(FileRecord.Create(path, directory, size, modified), score, reason);

    private static string GetScreenshotDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("LISTARYOPEN_SCREENSHOT_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ListaryOpen.sln")))
        {
            current = current.Parent;
        }

        return Path.Combine(
            current?.FullName ?? Path.GetTempPath(),
            "artifacts",
            "acceptance-results",
            "visual-evidence");
    }

    private static void Capture(
        Window window,
        string directory,
        string name,
        string step,
        ICollection<VisualEvidence> evidence)
    {
        window.UpdateLayout();
        var expected = LoadExpectedMatrix().Screenshots.Single(item => item.Id == name);
        Assert.Equal(expected.Theme, ThemeManager.Current.ToString());
        Assert.Equal(expected.Language, LocalizationManager.EffectiveLanguage.ToString());

        var handle = new WindowInteropHelper(window).EnsureHandle();
        Assert.NotEqual(IntPtr.Zero, handle);
        Assert.True(IsWindowVisible(handle), $"Visual evidence window '{name}' is not visible on the desktop.");
        var originalTopmost = window.Topmost;
        window.Topmost = true;
        Assert.True(SetWindowPos(
            handle,
            new IntPtr(-1),
            0,
            0,
            0,
            0,
            SetWindowPosNoMove | SetWindowPosNoSize | SetWindowPosShowWindow));
        _ = window.Activate();
        _ = SetForegroundWindow(handle);
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
        _ = DwmFlush();
        Thread.Sleep(250);
        _ = DwmFlush();
        Assert.True(GetWindowRect(handle, out var bounds));
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        Assert.InRange(width, expected.MinWidth, 4_000);
        Assert.InRange(height, expected.MinHeight, 2_500);
        var centerWindow = GetAncestor(
            WindowFromPoint(new NativePoint(bounds.Left + (width / 2), bounds.Top + (height / 2))),
            GetAncestorRoot);
        if (centerWindow != handle && IsExternalTopLevelWindow(centerWindow))
        {
            MinimizedExternalWindows.Value!.Add(centerWindow);
            _ = ShowWindow(centerWindow, ShowWindowMinimize);
            Assert.True(
                WaitForDispatcherCondition(
                    () => GetAncestor(
                        WindowFromPoint(new NativePoint(bounds.Left + (width / 2), bounds.Top + (height / 2))),
                        GetAncestorRoot) == handle,
                    TimeSpan.FromSeconds(3)),
                $"External window 0x{centerWindow.ToInt64():X} continued to occlude visual evidence '{name}' after minimization.");
            centerWindow = handle;
            _ = DwmFlush();
        }
        Assert.Equal(handle, centerWindow);

        var desktopDc = GetDC(IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, desktopDc);
        var memoryDc = CreateCompatibleDC(desktopDc);
        var bitmapHandle = CreateCompatibleBitmap(desktopDc, width, height);
        Assert.NotEqual(IntPtr.Zero, memoryDc);
        Assert.NotEqual(IntPtr.Zero, bitmapHandle);
        var previous = SelectObject(memoryDc, bitmapHandle);
        BitmapSource bitmap;
        try
        {
            Assert.True(
                BitBlt(memoryDc, 0, 0, width, height, desktopDc, bounds.Left, bounds.Top, SourceCopy | CaptureBlt),
                $"Desktop capture failed for '{name}'.");
            bitmap = Imaging.CreateBitmapSourceFromHBitmap(
                bitmapHandle,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
        }
        finally
        {
            _ = SelectObject(memoryDc, previous);
            _ = DeleteObject(bitmapHandle);
            _ = DeleteDC(memoryDc);
            _ = ReleaseDC(IntPtr.Zero, desktopDc);
        }

        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        var colors = new HashSet<int>();
        var sampleStep = Math.Max(4, pixels.Length / 20_000);
        sampleStep -= sampleStep % 4;
        for (var offset = 0; offset + 3 < pixels.Length; offset += sampleStep)
        {
            colors.Add(pixels[offset] | (pixels[offset + 1] << 8) | (pixels[offset + 2] << 16));
        }

        Assert.True(colors.Count > 16, $"Desktop screenshot '{name}' is a nearly uniform surface.");
        var bottomBlackRows = CountBottomUniformBlackRows(pixels, width, height, stride);
        Assert.InRange(bottomBlackRows, 0, expected.MaxBottomBlackRows);
        if (string.Equals(expected.Window, "QuickSwitch", StringComparison.Ordinal))
        {
            AssertQuickSwitchHasNoBrightOuterBand(pixels, width, height, stride, name);
        }

        var path = Path.Combine(directory, expected.File);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        var info = new FileInfo(path);
        Assert.True(info.Length > 1_024, $"Screenshot '{path}' is unexpectedly small.");
        evidence.Add(new VisualEvidence(
            name,
            step,
            Path.GetFileName(path),
            width,
            height,
            info.Length,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            expected.Window,
            expected.State,
            expected.Theme,
            expected.Language,
            "DesktopBitBlt",
            bottomBlackRows));
        window.Topmost = originalTopmost;
        if (!originalTopmost)
        {
            _ = SetWindowPos(
                handle,
                new IntPtr(-2),
                0,
                0,
                0,
                0,
                SetWindowPosNoMove | SetWindowPosNoSize);
        }
    }

    private static int CountBottomUniformBlackRows(byte[] pixels, int width, int height, int stride)
    {
        var rows = 0;
        for (var y = height - 1; y >= 0; y--)
        {
            var black = 0;
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var offset = row + (x * 4);
                if (pixels[offset] <= 3 && pixels[offset + 1] <= 3 && pixels[offset + 2] <= 3)
                {
                    black++;
                }
            }

            if (black < width * 0.98)
            {
                break;
            }

            rows++;
        }

        return rows;
    }

    private static void AssertQuickSwitchHasNoBrightOuterBand(
        byte[] pixels,
        int width,
        int height,
        int stride,
        string name)
    {
        const int edgeDepth = 6;
        const double maximumBrightFraction = 0.70;
        var inspectedRows = Math.Min(edgeDepth, height);
        var inspectedColumns = Math.Min(edgeDepth, width);

        for (var y = 0; y < inspectedRows; y++)
        {
            Assert.True(
                BrightPixelFractionInRow(pixels, width, stride, y) < maximumBrightFraction,
                $"Quick Switch screenshot '{name}' contains a bright top-edge band at row {y}.");
        }

        for (var y = Math.Max(0, height - inspectedRows); y < height; y++)
        {
            Assert.True(
                BrightPixelFractionInRow(pixels, width, stride, y) < maximumBrightFraction,
                $"Quick Switch screenshot '{name}' contains a bright bottom-edge band at row {y}.");
        }

        for (var x = 0; x < inspectedColumns; x++)
        {
            Assert.True(
                BrightPixelFractionInColumn(pixels, height, stride, x) < maximumBrightFraction,
                $"Quick Switch screenshot '{name}' contains a bright left-edge band at column {x}.");
        }

        for (var x = Math.Max(0, width - inspectedColumns); x < width; x++)
        {
            Assert.True(
                BrightPixelFractionInColumn(pixels, height, stride, x) < maximumBrightFraction,
                $"Quick Switch screenshot '{name}' contains a bright right-edge band at column {x}.");
        }
    }

    private static double BrightPixelFractionInRow(byte[] pixels, int width, int stride, int y)
    {
        var bright = 0;
        var row = y * stride;
        for (var x = 0; x < width; x++)
        {
            if (IsBrightPixel(pixels, row + (x * 4)))
            {
                bright++;
            }
        }

        return bright / (double)width;
    }

    private static double BrightPixelFractionInColumn(byte[] pixels, int height, int stride, int x)
    {
        var bright = 0;
        for (var y = 0; y < height; y++)
        {
            if (IsBrightPixel(pixels, (y * stride) + (x * 4)))
            {
                bright++;
            }
        }

        return bright / (double)height;
    }

    private static bool IsBrightPixel(byte[] pixels, int offset) =>
        pixels[offset] >= 235 && pixels[offset + 1] >= 235 && pixels[offset + 2] >= 235;

    private static bool IsExternalTopLevelWindow(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        return processId != 0 && processId != Environment.ProcessId;
    }

    private static void RestoreMinimizedExternalWindows()
    {
        var windows = MinimizedExternalWindows.Value;
        if (windows is null)
        {
            return;
        }

        foreach (var window in windows)
        {
            if (IsWindow(window))
            {
                _ = ShowWindow(window, ShowWindowRestore);
            }
        }
        windows.Clear();
    }

    private static ExpectedVisualMatrix LoadExpectedMatrix()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "visual-acceptance-matrix.json");
        Assert.True(File.Exists(path), $"The authoritative visual acceptance matrix is missing: {path}");
        var matrix = JsonSerializer.Deserialize<ExpectedVisualMatrix>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(matrix);
        Assert.Equal(1, matrix.SchemaVersion);
        Assert.Equal("DesktopBitBlt", matrix.CaptureMethod);
        Assert.Equal(22, matrix.Screenshots.Count);
        Assert.Equal(22, matrix.Screenshots.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(22, matrix.Screenshots.Select(item => item.File).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        return matrix;
    }

    private static void AssertEvidenceMatchesExpectedMatrix(IReadOnlyCollection<VisualEvidence> evidence)
    {
        var expected = LoadExpectedMatrix();
        Assert.Equal(expected.Screenshots.Select(item => item.Id), evidence.Select(item => item.Id));
        foreach (var state in expected.Screenshots)
        {
            var actual = Assert.Single(evidence, item => item.Id == state.Id);
            Assert.Equal(state.File, actual.File);
            Assert.Equal(state.Window, actual.Window);
            Assert.Equal(state.State, actual.State);
            Assert.Equal(state.Theme, actual.Theme);
            Assert.Equal(state.Language, actual.Language);
            Assert.Equal(expected.CaptureMethod, actual.CaptureMethod);
            Assert.InRange(actual.BottomUniformBlackRows, 0, state.MaxBottomBlackRows);
        }
    }

    private static void PumpLayout(Window window)
    {
        window.UpdateLayout();
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

    private static T? FindVisualDescendant<T>(DependencyObject root, Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && predicate(match))
            {
                return match;
            }

            var descendant = FindVisualDescendant(child, predicate);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static void CompleteWithDispatcher(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(
            _ => dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false)),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void AssertCompactSearchHasNoOuterBackgroundBand(SearchPanel panel)
    {
        // Compact mode uses transparent outer chrome + a rounded Surface border so Win10
        // corners stay anti-aliased (no hard SetWindowRgn). Shadow margin is 20 DIPs.
        var root = Assert.IsType<Grid>(panel.FindName("SearchPanelRoot"));
        Assert.Equal(new Thickness(0), root.Margin);
        Assert.Equal(72, panel.Height);
        Assert.Equal(System.Windows.Media.Brushes.Transparent, panel.Background);

        var chrome = Assert.IsType<Border>(panel.FindName("WindowChromeBorder"));
        Assert.Equal(new Thickness(10), chrome.Margin);
        Assert.Equal(new CornerRadius(12), chrome.CornerRadius);
        Assert.Same(panel.FindResource("Brush.Surface"), chrome.Background);

        // Input bar sits flush inside the outer rounded surface (no double chrome band).
        var inputChrome = Assert.IsType<Border>(panel.FindName("SearchInputChrome"));
        Assert.Equal(new CornerRadius(12), inputChrome.CornerRadius);
        Assert.Equal(new Thickness(0), inputChrome.BorderThickness);

        panel.UpdateLayout();
        // Sample inside the chrome face (past shadow margin), not the transparent edge.
        var width = (int)Math.Ceiling(panel.ActualWidth);
        var height = (int)Math.Ceiling(panel.ActualHeight);
        Assert.True(width > 40 && height > 40);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(panel);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var insetX = Math.Min(width / 2, 24);
        var midY = height / 2;
        var nearChromeEdge = ((midY * width) + insetX) * 4;
        var centerOffset = ((midY * width) + (width / 2)) * 4;
        var distance = Math.Abs(pixels[nearChromeEdge] - pixels[centerOffset]) +
            Math.Abs(pixels[nearChromeEdge + 1] - pixels[centerOffset + 1]) +
            Math.Abs(pixels[nearChromeEdge + 2] - pixels[centerOffset + 2]);
        Assert.InRange(distance, 0, 8);
    }

    private sealed record VisualEvidence(
        string Id,
        string Step,
        string File,
        int Width,
        int Height,
        long Size,
        string Sha256,
        string Window,
        string State,
        string Theme,
        string Language,
        string CaptureMethod,
        int BottomUniformBlackRows);

    private sealed record ExpectedVisualMatrix(
        int SchemaVersion,
        string CaptureMethod,
        IReadOnlyList<ExpectedVisualState> Screenshots);

    private sealed record ExpectedVisualState(
        string Id,
        string File,
        string Window,
        string State,
        string Theme,
        string Language,
        int MinWidth,
        int MinHeight,
        int MaxBottomBlackRows);

    private sealed class StaticSearchIndex(IReadOnlyList<SearchResult> results) : ISearchIndex
    {
        public Task UpsertAsync(FileRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(string fullPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordUsageAsync(string fullPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<SearchResult>> GetRecentAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SearchResult>>(results.Take(limit).ToArray());

        public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SearchResult>>(
                query.EffectiveMode == SearchMode.FoldersOnly
                    ? results.Where(result => result.Record.IsDirectory).ToArray()
                    : results);
    }

    private sealed class StaticTaskManagerAutomationService(IReadOnlyList<TaskManagerItem> items)
        : ITaskManagerAutomationService
    {
        public TaskManagerItem? SelectedItem { get; private set; }

        public bool ActivateSelection { get; private set; }

        public Task<IReadOnlyList<TaskManagerItem>> GetItemsAsync(
            IntPtr taskManagerWindow,
            CancellationToken cancellationToken) => Task.FromResult(items);

        public void QueueSelectItem(IntPtr taskManagerWindow, TaskManagerItem item, bool activate = false)
        {
            SelectedItem = item;
            ActivateSelection = activate;
        }

        public void CancelPending()
        {
        }

        public void Dispose()
        {
        }
    }

    private const uint SourceCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const uint GetAncestorRoot = 2;
    private const uint SetWindowPosNoSize = 0x0001;
    private const uint SetWindowPosNoMove = 0x0002;
    private const uint SetWindowPosShowWindow = 0x0040;
    private const int ShowWindowMinimize = 6;
    private const int ShowWindowRestore = 9;
    private static readonly ThreadLocal<HashSet<IntPtr>> MinimizedExternalWindows =
        new(() => new HashSet<IntPtr>());

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr graphicObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        IntPtr destination,
        int destinationX,
        int destinationY,
        int width,
        int height,
        IntPtr source,
        int sourceX,
        int sourceY,
        uint operation);
}
