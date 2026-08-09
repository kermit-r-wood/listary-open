using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Settings;
using ListaryOpen.Infrastructure.AppData;
using ListaryOpen.Infrastructure.Search.NameTable;

namespace ListaryOpen.IntegrationTests;

[Collection(DesktopIntegrationCollection.Name)]
public sealed class PackagedGlobalSearchBlackboxIntegrationTests
{
    private const ushort VkControl = 0x11;
    private const ushort VkSpace = 0x20;
    private const ushort VkA = 0x41;
    private const ushort VkBack = 0x08;
    private const ushort VkDown = 0x28;
    private const ushort VkRight = 0x27;
    private const ushort VkEscape = 0x1B;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const uint InputKeyboard = 1;
    private const uint SourceCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;

    [Fact]
    [Trait("Category", "ElevatedPackagedBlackboxE2E")]
    public async Task PublishedAppGlobalHotkeyDrivesSearchPreviewContextMenuEscapeAndQuickLaunch()
    {
        Assert.True(
            IsCurrentProcessElevated(),
            "ElevatedPackagedBlackboxE2E must run elevated; a medium-integrity run is a failure, not a skip.");
        var packageValue = Environment.GetEnvironmentVariable("LISTARYOPEN_PACKAGE_DIR");
        Assert.False(string.IsNullOrWhiteSpace(packageValue), "LISTARYOPEN_PACKAGE_DIR is required.");
        var packageDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageValue));
        var packageExePath = Path.Combine(packageDirectory, "ListaryOpen.App.exe");
        Assert.True(File.Exists(packageExePath), $"The packaged app executable is missing: {packageExePath}");
        Assert.True(
            IsSingleInstanceMutexAvailable(),
            "ListaryOpen is already running. This test will not terminate a user-owned instance.");

        var packageExeSha256 = Sha256(packageExePath);
        using var data = PackageDataScope.RequireInitiallyAbsent(packageDirectory);
        using var fixture = TemporaryDirectory.Create("listary-packaged-global-search");
        var nonce = Guid.NewGuid().ToString("N");
        var query = "globalsearch" + nonce[..10];
        var firstName = query + "-alpha.txt";
        var secondName = query + "-beta.txt";
        var firstPath = Path.Combine(fixture.Path, firstName);
        var secondPath = Path.Combine(fixture.Path, secondName);
        var firstPreview = "PREVIEW-ALPHA-" + nonce;
        var secondPreview = "PREVIEW-BETA-" + nonce;
        await File.WriteAllTextAsync(firstPath, firstPreview);
        await File.WriteAllTextAsync(secondPath, secondPreview);
        var markerPath = Path.Combine(fixture.Path, "quick-launch-marker-" + nonce + ".txt");
        var markerValue = "QUICK-LAUNCH-" + nonce;
        var quickLaunchKeyword = "ql" + nonce[..12];

        data.Create();
        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(powershellPath));
        var script = $"[IO.File]::WriteAllText('{EscapePowerShellLiteral(markerPath)}','{markerValue}')";
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var defaults = AppSettings.Defaults();
        var settings = defaults.WithPreferences(
            [fixture.Path],
            [],
            AppTheme.Light,
            IndexUpdateFrequency.StartupOnly,
            defaults.QuickMenuEntries,
            checkForUpdates: false,
            quickLaunchEntries:
            [
                new QuickLaunchEntry(
                    "packaged-e2e-marker",
                    quickLaunchKeyword,
                    "Write packaged E2E marker",
                    powershellPath,
                    Enabled: true,
                    Arguments: "-NoProfile -NonInteractive -EncodedCommand " + encodedCommand,
                    WorkingDirectory: fixture.Path,
                    Silent: true,
                    RunAsAdmin: false)
            ],
            searchTransliteration: SearchTransliterationMode.Disabled,
            language: AppLanguage.English);
        new AppSettingsStore().Save(data.SettingsPath, settings);
        await using (var index = await NameTableSearchIndex.OpenAsync(data.LosnPath, CancellationToken.None))
        {
            await index.UpsertAsync(
                FileRecord.Create(firstPath, false, new FileInfo(firstPath).Length, File.GetLastWriteTimeUtc(firstPath)),
                CancellationToken.None);
            await index.UpsertAsync(
                FileRecord.Create(secondPath, false, new FileInfo(secondPath).Length, File.GetLastWriteTimeUtc(secondPath)),
                CancellationToken.None);
            Assert.True(await index.FlushDurableSnapshotAsync(CancellationToken.None));
        }

        var controlNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        Process? app = null;
        E2ePipeClient? control = null;
        var shutdownSucceeded = false;
        try
        {
            app = Process.Start(new ProcessStartInfo
            {
                FileName = packageExePath,
                Arguments = "--listary-e2e-control=" + controlNonce,
                WorkingDirectory = packageDirectory,
                UseShellExecute = false
            });
            Assert.NotNull(app);
            control = E2ePipeClient.Connect("ListaryOpen.E2E." + controlNonce, app, TimeSpan.FromSeconds(45));
            Assert.Equal($"Ready {app.Id}", control.Exchange(controlNonce + " Ready"));
            Assert.Equal("Ok", control.Exchange(controlNonce + " AllowInjectedInput"));

            SendHotkey(VkControl, VkSpace);
            var searchWindow = WaitForWindow(app.Id, "ListaryOpen Search", TimeSpan.FromSeconds(10));
            var root = AutomationElement.FromHandle(searchWindow);
            Assert.NotNull(root);
            Assert.Equal("GlobalSearchWindow", root.Current.AutomationId);
            var queryElement = WaitForAutomationId(root, "GlobalSearchQuery", TimeSpan.FromSeconds(5));
            var statusElement = WaitForAutomationId(root, "GlobalSearchStatus", TimeSpan.FromSeconds(5));
            Assert.Equal("GlobalSearchQuery", queryElement.Current.AutomationId);
            Assert.Equal("GlobalSearchStatus", statusElement.Current.AutomationId);

            SendText(query);
            Assert.True(
                WaitUntil(() => string.Equals(ReadValue(queryElement), query, StringComparison.Ordinal), TimeSpan.FromSeconds(5)),
                "The packaged global-search query did not receive the real SendInput text.");
            var resultsElement = WaitForAutomationId(root, "GlobalSearchResults", TimeSpan.FromSeconds(12));
            Assert.Equal("GlobalSearchResults", resultsElement.Current.AutomationId);
            Assert.True(
                WaitUntil(() =>
                    ContainsExactName(resultsElement, firstName) &&
                    ContainsExactName(resultsElement, secondName) &&
                    TryReadSelectedResultName(resultsElement, [firstName, secondName], out _),
                    TimeSpan.FromSeconds(12)),
                "The preseeded real index did not publish both expected results through UI Automation.");

            Assert.True(TryReadSelectedResultName(resultsElement, [firstName, secondName], out var initialSelection));
            Assert.Contains(initialSelection, new[] { firstName, secondName });
            SendKey(VkDown);
            string? movedSelection = null;
            Assert.True(
                WaitUntil(() =>
                    TryReadSelectedResultName(resultsElement, [firstName, secondName], out movedSelection) &&
                    !string.Equals(initialSelection, movedSelection, StringComparison.Ordinal),
                    TimeSpan.FromSeconds(5)),
                "Down did not move selection to the other indexed result.");
            Assert.NotNull(movedSelection);
            var expectedPreview = string.Equals(movedSelection, firstName, StringComparison.Ordinal)
                ? firstPreview
                : secondPreview;
            var previewElement = WaitForAutomationId(root, "GlobalSearchPreview", TimeSpan.FromSeconds(8));
            Assert.Equal("GlobalSearchPreview", previewElement.Current.AutomationId);
            Assert.True(
                WaitUntil(() =>
                    !previewElement.Current.BoundingRectangle.IsEmpty &&
                    ContainsExactName(previewElement, movedSelection!) &&
                    ReadAnyTextValue(previewElement).Contains(expectedPreview, StringComparison.Ordinal),
                    TimeSpan.FromSeconds(8)),
                "The preview did not show the exact content of the keyboard-selected real file.");

            SendKey(VkRight);
            AutomationElement? contextMenu = null;
            Assert.True(
                WaitUntil(() =>
                {
                    contextMenu = FindResultContextMenu(app.Id);
                    return contextMenu is not null;
                }, TimeSpan.FromSeconds(5)),
                "Right did not open the selected result context menu.");
            Assert.NotNull(contextMenu);
            Assert.True(ContainsExactName(contextMenu, "Open"));
            Assert.True(ContainsExactName(contextMenu, "Show in File Explorer"));
            Assert.True(ContainsExactName(contextMenu, "Copy full path"));
            Assert.True(GetWindowRect(searchWindow, out var searchBounds));
            var menuBounds = ToNativeRect(contextMenu.Current.BoundingRectangle);
            Assert.True(menuBounds.Right > menuBounds.Left && menuBounds.Bottom > menuBounds.Top);
            var screenshotPath = CaptureEvidence(
                searchBounds,
                menuBounds,
                "31-packaged-global-search-context-menu");

            FocusOwningWindow(contextMenu);
            SendKey(VkEscape);
            Assert.True(
                WaitUntil(() => FindResultContextMenu(app.Id) is null, TimeSpan.FromSeconds(5)),
                $"The first Escape did not close the result context menu (search visible: {IsWindowVisible(searchWindow)}).");
            Assert.True(IsWindowVisible(searchWindow), "Closing the context menu also hid the search window.");
            SendKey(VkEscape);
            Assert.True(
                WaitUntil(() => !IsWindowVisible(searchWindow), TimeSpan.FromSeconds(5)),
                "The second Escape did not hide global search.");

            SendHotkey(VkControl, VkSpace);
            Assert.True(WaitUntil(() => IsWindowVisible(searchWindow), TimeSpan.FromSeconds(5)));
            root = AutomationElement.FromHandle(searchWindow);
            queryElement = WaitForAutomationId(root, "GlobalSearchQuery", TimeSpan.FromSeconds(5));
            SendHotkey(VkControl, VkA);
            SendKey(VkBack);
            Assert.True(WaitUntil(() => ReadValue(queryElement).Length == 0, TimeSpan.FromSeconds(3)));
            SendText(quickLaunchKeyword);
            Assert.True(
                WaitUntil(() =>
                    File.Exists(markerPath) &&
                    string.Equals(File.ReadAllText(markerPath), markerValue, StringComparison.Ordinal),
                    TimeSpan.FromSeconds(12)),
                "The production QuickLaunchExecutor did not create the external marker.");
            Assert.True(
                WaitUntil(() => !IsWindowVisible(searchWindow), TimeSpan.FromSeconds(5)),
                "Successful exact-keyword Quick Launch did not dismiss compact global search.");

            WriteMetadata(
                screenshotPath,
                packageExePath,
                packageExeSha256,
                app.Id,
                data,
                fixture.Path,
                query,
                firstName,
                secondName,
                initialSelection!,
                movedSelection!,
                expectedPreview,
                quickLaunchKeyword,
                markerPath,
                markerValue,
                searchBounds,
                menuBounds);

            Assert.Equal("Ok", control.Exchange(controlNonce + " Shutdown"));
            shutdownSucceeded = app.WaitForExit(15_000);
            Assert.True(shutdownSucceeded, "The packaged app did not exit after authenticated Shutdown.");
            Assert.Equal(0, app.ExitCode);
        }
        finally
        {
            if (app is { HasExited: false })
            {
                if (!shutdownSucceeded && control is not null)
                {
                    try
                    {
                        _ = control.Exchange(controlNonce + " Shutdown");
                        shutdownSucceeded = app.WaitForExit(10_000);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidOperationException)
                    {
                    }
                }
                if (!app.HasExited)
                {
                    app.Kill(entireProcessTree: true);
                    _ = app.WaitForExit(10_000);
                }
            }
            control?.Dispose();
            app?.Dispose();
        }

        Assert.Equal(packageExeSha256, Sha256(packageExePath));
    }

    private static AutomationElement WaitForAutomationId(AutomationElement root, string id, TimeSpan timeout)
    {
        AutomationElement? element = null;
        Assert.True(
            WaitUntil(() =>
            {
                try
                {
                    element = root.FindFirst(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, id));
                    return element is not null;
                }
                catch (ElementNotAvailableException)
                {
                    return false;
                }
            }, timeout),
            $"UI Automation element '{id}' did not appear.");
        return element!;
    }

    private static string ReadValue(AutomationElement element)
    {
        Assert.True(element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern));
        return Assert.IsType<ValuePattern>(pattern).Current.Value;
    }

    private static bool ContainsExactName(AutomationElement root, string name)
    {
        try
        {
            return root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, name)) is not null;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static bool TryReadSelectedResultName(
        AutomationElement results,
        IReadOnlyList<string> expectedNames,
        out string? selectedName)
    {
        selectedName = null;
        try
        {
            if (!results.TryGetCurrentPattern(SelectionPattern.Pattern, out var pattern))
            {
                return false;
            }
            var selected = ((SelectionPattern)pattern).Current.GetSelection();
            if (selected.Length != 1)
            {
                return false;
            }
            selectedName = expectedNames.FirstOrDefault(name => ContainsExactName(selected[0], name));
            return selectedName is not null;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static string ReadAnyTextValue(AutomationElement root)
    {
        try
        {
            foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
            {
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
                {
                    var value = ((ValuePattern)valuePattern).Current.Value;
                    if (!string.IsNullOrEmpty(value))
                    {
                        return value;
                    }
                }
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern))
                {
                    var value = ((TextPattern)textPattern).DocumentRange.GetText(-1);
                    if (!string.IsNullOrEmpty(value))
                    {
                        return value;
                    }
                }
            }
        }
        catch (ElementNotAvailableException)
        {
        }
        return string.Empty;
    }

    private static AutomationElement? FindResultContextMenu(int processId)
    {
        try
        {
            foreach (AutomationElement element in AutomationElement.RootElement.FindAll(
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu))))
            {
                if (ContainsExactName(element, "Open") &&
                    ContainsExactName(element, "Show in File Explorer") &&
                    ContainsExactName(element, "Copy full path") &&
                    !element.Current.IsOffscreen &&
                    !element.Current.BoundingRectangle.IsEmpty &&
                    HasVisiblePopupWindow(element))
                {
                    return element;
                }
            }
        }
        catch (ElementNotAvailableException)
        {
        }
        return null;
    }

    private static bool HasVisiblePopupWindow(AutomationElement element)
    {
        var current = element;
        while ((current = TreeWalker.ControlViewWalker.GetParent(current)) is not null &&
               !Automation.Compare(current, AutomationElement.RootElement))
        {
            if (current.Current.ControlType == ControlType.Window)
            {
                var handle = new IntPtr(current.Current.NativeWindowHandle);
                return handle != IntPtr.Zero && IsWindowVisible(handle);
            }
        }

        return false;
    }

    private static void FocusOwningWindow(AutomationElement element)
    {
        var current = element;
        while ((current = TreeWalker.ControlViewWalker.GetParent(current)) is not null &&
               !Automation.Compare(current, AutomationElement.RootElement))
        {
            if (current.Current.ControlType != ControlType.Window)
            {
                continue;
            }

            var handle = new IntPtr(current.Current.NativeWindowHandle);
            if (handle != IntPtr.Zero)
            {
                Assert.True(SetForegroundWindow(handle));
            }
            return;
        }
    }

    private static IntPtr WaitForWindow(int processId, string title, TimeSpan timeout)
    {
        var result = IntPtr.Zero;
        Assert.True(
            WaitUntil(() =>
            {
                _ = EnumWindows((window, parameter) =>
                {
                    _ = GetWindowThreadProcessId(window, out var owner);
                    if (owner == unchecked((uint)processId) &&
                        IsWindowVisible(window) &&
                        string.Equals(ReadWindowText(window), title, StringComparison.Ordinal))
                    {
                        result = window;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
                return result != IntPtr.Zero;
            }, timeout),
            $"The packaged app did not show '{title}'.");
        return result;
    }

    private static void SendHotkey(ushort modifier, ushort key)
    {
        SendKeyboardInput(modifier, keyUp: false);
        SendKeyboardInput(key, keyUp: false);
        SendKeyboardInput(key, keyUp: true);
        SendKeyboardInput(modifier, keyUp: true);
    }

    private static void SendKey(ushort key)
    {
        SendKeyboardInput(key, keyUp: false);
        SendKeyboardInput(key, keyUp: true);
    }

    private static void SendText(string value)
    {
        foreach (var character in value)
        {
            SendUnicodeInput(character, keyUp: false);
            SendUnicodeInput(character, keyUp: true);
        }
    }

    private static void SendUnicodeInput(char character, bool keyUp)
    {
        var flags = KeyEventUnicode | (keyUp ? KeyEventKeyUp : 0);
        var inputs = new[] { NativeInput.Keyboard(0, character, flags) };
        Assert.Equal(1u, SendInput(1, inputs, Marshal.SizeOf<NativeInput>()));
    }

    private static void SendKeyboardInput(ushort key, bool keyUp)
    {
        var inputs = new[] { NativeInput.Keyboard(key, '\0', keyUp ? KeyEventKeyUp : 0) };
        Assert.Equal(1u, SendInput(1, inputs, Marshal.SizeOf<NativeInput>()));
    }

    private static string CaptureEvidence(NativeRect first, NativeRect second, string name)
    {
        var directory = Environment.GetEnvironmentVariable("LISTARYOPEN_SCREENSHOT_DIR");
        directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(FindRepositoryRoot(), "artifacts", "acceptance-results", "visual-evidence")
            : Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        _ = DwmFlush();
        Thread.Sleep(150);
        var left = Math.Min(first.Left, second.Left) - 12;
        var top = Math.Min(first.Top, second.Top) - 12;
        var right = Math.Max(first.Right, second.Right) + 12;
        var bottom = Math.Max(first.Bottom, second.Bottom) + 12;
        var width = right - left;
        var height = bottom - top;
        var desktop = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(desktop);
        var bitmap = CreateCompatibleBitmap(desktop, width, height);
        var previous = SelectObject(memory, bitmap);
        try
        {
            Assert.True(BitBlt(memory, 0, 0, width, height, desktop, left, top, SourceCopy | CaptureBlt));
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            var path = Path.Combine(directory, name + ".png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = File.Create(path);
            encoder.Save(stream);
            stream.Flush();
            Assert.True(stream.Length > 10_000);
            return path;
        }
        finally
        {
            _ = SelectObject(memory, previous);
            _ = DeleteObject(bitmap);
            _ = DeleteDC(memory);
            _ = ReleaseDC(IntPtr.Zero, desktop);
        }
    }

    private static void WriteMetadata(
        string screenshotPath,
        string packageExePath,
        string packageExeSha256,
        int appProcessId,
        PackageDataScope data,
        string fixtureRoot,
        string query,
        string firstResult,
        string secondResult,
        string initialSelection,
        string movedSelection,
        string previewText,
        string quickLaunchKeyword,
        string markerPath,
        string markerValue,
        NativeRect searchBounds,
        NativeRect menuBounds)
    {
        var metadataPath = Path.Combine(Path.GetDirectoryName(screenshotPath)!, "31-packaged-global-search-context-menu.json");
        var metadata = new
        {
            schemaVersion = 1,
            scenario = "packaged-elevated-global-hotkey-search-preview-menu-and-quick-launch",
            passed = true,
            package = new { executable = packageExePath, executableSha256 = packageExeSha256, processId = appProcessId },
            preseed = new
            {
                packageLocalSettings = data.SettingsPath,
                packageLocalIndex = data.LosnPath,
                fixtureRoot,
                indexedResults = new[] { firstResult, secondResult }
            },
            input = new
            {
                transport = "WindowsSendInput",
                globalHotkey = "Ctrl+Space",
                query,
                automationIds = new[]
                {
                    "GlobalSearchWindow", "GlobalSearchQuery", "GlobalSearchResults",
                    "GlobalSearchPreview", "GlobalSearchStatus"
                }
            },
            searchOracle = new
            {
                bothIndexedResultsVisible = true,
                initialSelection,
                downSelection = movedSelection,
                selectionChanged = initialSelection != movedSelection,
                previewExactText = previewText,
                contextMenuItems = new[] { "Open / switch", "Show in File Explorer", "Copy full path" },
                firstEscapeClosedMenuOnly = true,
                secondEscapeDismissedSearch = true
            },
            quickLaunchOracle = new
            {
                keyword = quickLaunchKeyword,
                productionExecutor = true,
                markerPath,
                markerValue,
                markerExact = File.Exists(markerPath) && File.ReadAllText(markerPath) == markerValue,
                searchDismissedAfterLaunch = true
            },
            visualOracle = new
            {
                searchBounds = Bounds(searchBounds),
                contextMenuBounds = Bounds(menuBounds),
                screenshot = Path.GetFileName(screenshotPath),
                screenshotSha256 = Sha256(screenshotPath)
            }
        };
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(new FileInfo(metadataPath).Length > 1_000);
    }

    private static object Bounds(NativeRect value) => new
    {
        left = value.Left,
        top = value.Top,
        right = value.Right,
        bottom = value.Bottom,
        width = value.Right - value.Left,
        height = value.Bottom - value.Top
    };

    private static NativeRect ToNativeRect(System.Windows.Rect value) => new()
    {
        Left = checked((int)Math.Floor(value.Left)),
        Top = checked((int)Math.Floor(value.Top)),
        Right = checked((int)Math.Ceiling(value.Right)),
        Bottom = checked((int)Math.Ceiling(value.Bottom))
    };

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition()) return true;
            Thread.Sleep(100);
        }
        return condition();
    }

    private static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool IsSingleInstanceMutexAvailable()
    {
        using var mutex = new Mutex(false, @"Local\ListaryOpen.SingleInstance");
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.Zero); }
            catch (AbandonedMutexException) { acquired = true; }
            return acquired;
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private static string ReadWindowText(IntPtr window)
    {
        var buffer = new StringBuilder(512);
        _ = GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ListaryOpen.sln"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class E2ePipeClient : IDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        private E2ePipeClient(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
            _reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 256, true);
            _writer = new StreamWriter(pipe, new UTF8Encoding(false), 256, true) { AutoFlush = true, NewLine = "\n" };
        }

        public static E2ePipeClient Connect(string pipeName, Process app, TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            Exception? lastError = null;
            while (stopwatch.Elapsed < timeout)
            {
                app.Refresh();
                if (app.HasExited) throw new InvalidOperationException($"The packaged app exited with code {app.ExitCode} before Ready.", lastError);
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
                try
                {
                    pipe.Connect(750);
                    return new E2ePipeClient(pipe);
                }
                catch (Exception exception) when (exception is TimeoutException or IOException)
                {
                    lastError = exception;
                    pipe.Dispose();
                }
            }
            throw new TimeoutException("The packaged app did not create its E2E endpoint.", lastError);
        }

        public string Exchange(string request)
        {
            _writer.WriteLine(request);
            return _reader.ReadLine() ?? throw new IOException("The app closed the E2E pipe without a response.");
        }

        public void Dispose()
        {
            _writer.Dispose();
            _reader.Dispose();
            _pipe.Dispose();
        }
    }

    private sealed class PackageDataScope : IDisposable
    {
        private readonly string _packageDirectory;

        private PackageDataScope(string packageDirectory, string dataDirectory)
        {
            _packageDirectory = packageDirectory;
            DataDirectory = dataDirectory;
        }

        public string DataDirectory { get; }
        public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
        public string LosnPath => Path.Combine(DataDirectory, "index.losn");

        public static PackageDataScope RequireInitiallyAbsent(string packageDirectory)
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDirectory));
            var data = Path.GetFullPath(Path.Combine(normalized, "data"));
            Assert.Equal(normalized, Path.GetDirectoryName(data), ignoreCase: true);
            Assert.False(Directory.Exists(data), $"Package data must initially be absent: {data}");
            return new PackageDataScope(normalized, data);
        }

        public void Create()
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(Path.Combine(DataDirectory, "tmp"));
        }

        public void Dispose()
        {
            if (!Directory.Exists(DataDirectory)) return;
            Assert.Equal(_packageDirectory, Path.GetDirectoryName(DataDirectory), ignoreCase: true);
            Assert.False((File.GetAttributes(DataDirectory) & FileAttributes.ReparsePoint) != 0);
            Directory.Delete(DataDirectory, recursive: true);
            Assert.False(Directory.Exists(DataDirectory));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
        }
        public string Path { get; }
        public static TemporaryDirectory Create(string prefix) => new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N")));
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public InputUnion Data;
        public static NativeInput Keyboard(ushort virtualKey, char scan, uint flags) => new()
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    ScanCode = scan,
                    Flags = flags
                }
            }
        };
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder value, int count);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
