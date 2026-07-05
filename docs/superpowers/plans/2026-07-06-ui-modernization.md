# UI Modernization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Modernize the ListaryOpen WPF search panel and settings window with native WPF styling while preserving tray-first and keyboard-first behavior.

**Architecture:** Add app-level WPF resource dictionaries, then update presentation-only ViewModel properties and XAML layouts in small increments. Keep search, indexing, hook quick switch, dialog jump, tray lifecycle, and activation business logic unchanged. Add WPF smoke tests and string-based XAML checks so visual resources and key bindings stay verifiable without screenshot infrastructure.

**Tech Stack:** .NET 8 WPF, XAML resource dictionaries, xUnit, STA-thread WPF smoke tests, PowerShell verification commands.

---

## Scope Check

The approved spec covers one cohesive UI modernization pass. Search panel and settings window share the same visual system and can be implemented in one plan with independent tasks. This plan avoids WinUI 3 migration, avoids WPF UI/ModernWpf dependencies, and does not change ranking, indexing, hook IPC, dialog automation, or tray lifecycle behavior.

## File Structure

Create or modify these UI resources:

- `src/ListaryOpen.App/Styles/Colors.xaml`: neutral palette, text brushes, border brushes, accent brushes, and status brushes.
- `src/ListaryOpen.App/Styles/Typography.xaml`: shared font family, font sizes, weights, and common text styles.
- `src/ListaryOpen.App/Styles/Controls.xaml`: shared button, badge, section, list, and focus-friendly primitive styles.
- `src/ListaryOpen.App/Styles/SearchPanel.xaml`: search-panel-specific input, result list, result row, mode bar, and status styles.
- `src/ListaryOpen.App/Styles/Settings.xaml`: settings-section and settings-row styles.
- `src/ListaryOpen.App/App.xaml`: merge the resource dictionaries and keep `ListaryOpenIcon`.
- `src/ListaryOpen.App/SearchPanel.xaml`: command-palette layout with mode bar, query box, richer result rows, and stable status area.
- `src/ListaryOpen.App/MainWindow.xaml`: grouped settings layout for Indexing, Hotkeys, Quick Switch, and General.

Modify these ViewModels only for presentation-derived properties:

- `src/ListaryOpen.App/ViewModels/SearchPanelViewModel.cs`: expose mode label and query placeholder.
- `src/ListaryOpen.App/ViewModels/SettingsViewModel.cs`: expose badge/action text for indexing, NTFS fast indexing, hook quick switch, and Quick Save/Open.

Create or modify these tests:

- `tests/ListaryOpen.Infrastructure.Tests/App/WpfSmokeTests.cs`: STA-thread smoke test for resource loading and window construction.
- `tests/ListaryOpen.Infrastructure.Tests/App/SearchPanelXamlTests.cs`: string checks for search-panel binding/layout expectations.
- `tests/ListaryOpen.Infrastructure.Tests/App/SettingsXamlTests.cs`: string checks for settings-section binding/layout expectations.
- `tests/ListaryOpen.Infrastructure.Tests/App/SearchPanelViewModelTests.cs`: presentation-mode tests.
- `tests/ListaryOpen.Infrastructure.Tests/App/SettingsViewModelTests.cs`: settings presentation property tests.
- `tests/ListaryOpen.Infrastructure.Tests/App/SettingsHookQuickSwitchTests.cs`: hook quick-switch badge/action tests.
- `docs/manual-test-checklist.md`: UI manual acceptance section.

Existing dirty worktree changes for UAC, hooks, and NTFS indexing must remain untouched unless they are directly required by a task in this plan. Every commit command below stages only the listed UI files.

## Task 1: Resource Dictionaries And WPF Smoke Coverage

**Files:**
- Create: `tests/ListaryOpen.Infrastructure.Tests/App/WpfSmokeTests.cs`
- Create: `src/ListaryOpen.App/Styles/Colors.xaml`
- Create: `src/ListaryOpen.App/Styles/Typography.xaml`
- Create: `src/ListaryOpen.App/Styles/Controls.xaml`
- Create: `src/ListaryOpen.App/Styles/SearchPanel.xaml`
- Create: `src/ListaryOpen.App/Styles/Settings.xaml`
- Modify: `src/ListaryOpen.App/App.xaml`
- Modify: `src/ListaryOpen.App/MainWindow.xaml`

- [ ] **Step 1: Write the failing WPF smoke test**

Create `tests/ListaryOpen.Infrastructure.Tests/App/WpfSmokeTests.cs`:

```csharp
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using ListaryOpen.App;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Core.Settings;
using WpfApp = ListaryOpen.App.App;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class WpfSmokeTests
{
    [Fact]
    public void UiResourcesAndWindowsLoadOnStaThread()
    {
        Exception? exception = null;
        var completed = false;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new WpfApp();

                Assert.NotNull(application.FindResource("Brush.AppBackground"));
                Assert.NotNull(application.FindResource("Brush.Surface"));
                Assert.NotNull(application.FindResource("Brush.TextPrimary"));
                Assert.NotNull(application.FindResource("SearchPanelResultListStyle"));
                Assert.NotNull(application.FindResource("SettingsSectionStyle"));

                var mainWindow = new MainWindow(new SettingsViewModel(AppSettings.Defaults()));
                Assert.NotNull(mainWindow.FindName("SettingsContentRoot"));

                var searchPanel = new SearchPanel();
                Assert.NotNull(searchPanel.FindName("QueryBox"));
                Assert.NotNull(searchPanel.FindName("ResultsList"));

                mainWindow.Hide();
                searchPanel.Hide();
                application.Shutdown();
            }
            catch (Exception caught)
            {
                exception = caught;
            }
            finally
            {
                completed = true;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10));

        Assert.True(completed);
        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
```

- [ ] **Step 2: Run the smoke test and verify it fails**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter WpfSmokeTests -p:UseAppHost=false -p:BaseOutputPath=.test-ui-smoke\
```

Expected: FAIL because `Brush.AppBackground`, `SearchPanelResultListStyle`, `SettingsSectionStyle`, and `SettingsContentRoot` do not exist yet.

- [ ] **Step 3: Add the neutral color resource dictionary**

Create `src/ListaryOpen.App/Styles/Colors.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Color x:Key="Color.AppBackground">#F7F8FA</Color>
    <Color x:Key="Color.Surface">#FFFFFFFF</Color>
    <Color x:Key="Color.SurfaceMuted">#FFF1F3F5</Color>
    <Color x:Key="Color.Border">#FFD8DEE6</Color>
    <Color x:Key="Color.TextPrimary">#FF111827</Color>
    <Color x:Key="Color.TextSecondary">#FF4B5563</Color>
    <Color x:Key="Color.TextMuted">#FF6B7280</Color>
    <Color x:Key="Color.Accent">#FF2563EB</Color>
    <Color x:Key="Color.AccentSoft">#FFEFF6FF</Color>
    <Color x:Key="Color.Success">#FF047857</Color>
    <Color x:Key="Color.SuccessSoft">#FFECFDF5</Color>
    <Color x:Key="Color.Warning">#FFB45309</Color>
    <Color x:Key="Color.WarningSoft">#FFFFFBEB</Color>
    <Color x:Key="Color.Error">#FFB91C1C</Color>
    <Color x:Key="Color.ErrorSoft">#FFFEF2F2</Color>
    <Color x:Key="Color.Selection">#FFE8F1FF</Color>

    <SolidColorBrush x:Key="Brush.AppBackground" Color="{StaticResource Color.AppBackground}" />
    <SolidColorBrush x:Key="Brush.Surface" Color="{StaticResource Color.Surface}" />
    <SolidColorBrush x:Key="Brush.SurfaceMuted" Color="{StaticResource Color.SurfaceMuted}" />
    <SolidColorBrush x:Key="Brush.Border" Color="{StaticResource Color.Border}" />
    <SolidColorBrush x:Key="Brush.TextPrimary" Color="{StaticResource Color.TextPrimary}" />
    <SolidColorBrush x:Key="Brush.TextSecondary" Color="{StaticResource Color.TextSecondary}" />
    <SolidColorBrush x:Key="Brush.TextMuted" Color="{StaticResource Color.TextMuted}" />
    <SolidColorBrush x:Key="Brush.Accent" Color="{StaticResource Color.Accent}" />
    <SolidColorBrush x:Key="Brush.AccentSoft" Color="{StaticResource Color.AccentSoft}" />
    <SolidColorBrush x:Key="Brush.Success" Color="{StaticResource Color.Success}" />
    <SolidColorBrush x:Key="Brush.SuccessSoft" Color="{StaticResource Color.SuccessSoft}" />
    <SolidColorBrush x:Key="Brush.Warning" Color="{StaticResource Color.Warning}" />
    <SolidColorBrush x:Key="Brush.WarningSoft" Color="{StaticResource Color.WarningSoft}" />
    <SolidColorBrush x:Key="Brush.Error" Color="{StaticResource Color.Error}" />
    <SolidColorBrush x:Key="Brush.ErrorSoft" Color="{StaticResource Color.ErrorSoft}" />
    <SolidColorBrush x:Key="Brush.Selection" Color="{StaticResource Color.Selection}" />
</ResourceDictionary>
```

- [ ] **Step 4: Add typography resources**

Create `src/ListaryOpen.App/Styles/Typography.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:sys="clr-namespace:System;assembly=System.Runtime">
    <FontFamily x:Key="Font.Primary">Segoe UI</FontFamily>
    <sys:Double x:Key="FontSize.WindowTitle">22</sys:Double>
    <sys:Double x:Key="FontSize.SectionTitle">15</sys:Double>
    <sys:Double x:Key="FontSize.Body">13</sys:Double>
    <sys:Double x:Key="FontSize.Small">12</sys:Double>
    <sys:Double x:Key="FontSize.SearchQuery">22</sys:Double>
    <sys:Double x:Key="FontSize.SearchTitle">14</sys:Double>

    <Style x:Key="Text.WindowTitle" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource Font.Primary}" />
        <Setter Property="FontSize" Value="{StaticResource FontSize.WindowTitle}" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="Foreground" Value="{StaticResource Brush.TextPrimary}" />
        <Setter Property="TextWrapping" Value="NoWrap" />
        <Setter Property="TextTrimming" Value="CharacterEllipsis" />
    </Style>

    <Style x:Key="Text.SectionTitle" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource Font.Primary}" />
        <Setter Property="FontSize" Value="{StaticResource FontSize.SectionTitle}" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="Foreground" Value="{StaticResource Brush.TextPrimary}" />
        <Setter Property="TextWrapping" Value="NoWrap" />
        <Setter Property="TextTrimming" Value="CharacterEllipsis" />
    </Style>

    <Style x:Key="Text.Body" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource Font.Primary}" />
        <Setter Property="FontSize" Value="{StaticResource FontSize.Body}" />
        <Setter Property="Foreground" Value="{StaticResource Brush.TextSecondary}" />
        <Setter Property="TextWrapping" Value="Wrap" />
    </Style>
</ResourceDictionary>
```

- [ ] **Step 5: Add shared control resources**

Create `src/ListaryOpen.App/Styles/Controls.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <CornerRadius x:Key="Radius.Control">6</CornerRadius>
    <CornerRadius x:Key="Radius.Section">8</CornerRadius>
    <Thickness x:Key="Thickness.ControlPadding">12,6</Thickness>
    <Thickness x:Key="Thickness.SectionPadding">16</Thickness>

    <Style x:Key="BadgeTextBlockStyle" TargetType="TextBlock">
        <Setter Property="MinWidth" Value="64" />
        <Setter Property="Padding" Value="8,3" />
        <Setter Property="TextAlignment" Value="Center" />
        <Setter Property="FontSize" Value="{StaticResource FontSize.Small}" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="Foreground" Value="{StaticResource Brush.TextSecondary}" />
        <Setter Property="Background" Value="{StaticResource Brush.SurfaceMuted}" />
    </Style>

    <Style x:Key="ActionButtonStyle" TargetType="Button">
        <Setter Property="MinWidth" Value="88" />
        <Setter Property="MinHeight" Value="32" />
        <Setter Property="Padding" Value="{StaticResource Thickness.ControlPadding}" />
        <Setter Property="FontFamily" Value="{StaticResource Font.Primary}" />
        <Setter Property="FontSize" Value="{StaticResource FontSize.Body}" />
    </Style>
</ResourceDictionary>
```

- [ ] **Step 6: Add placeholder search/settings style resources**

Create `src/ListaryOpen.App/Styles/SearchPanel.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Style x:Key="SearchPanelResultListStyle" TargetType="ListBox">
        <Setter Property="BorderBrush" Value="{StaticResource Brush.Border}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="Background" Value="{StaticResource Brush.Surface}" />
    </Style>
</ResourceDictionary>
```

Create `src/ListaryOpen.App/Styles/Settings.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Style x:Key="SettingsSectionStyle" TargetType="Border">
        <Setter Property="Background" Value="{StaticResource Brush.Surface}" />
        <Setter Property="BorderBrush" Value="{StaticResource Brush.Border}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="CornerRadius" Value="{StaticResource Radius.Section}" />
        <Setter Property="Padding" Value="{StaticResource Thickness.SectionPadding}" />
        <Setter Property="Margin" Value="0,0,0,12" />
    </Style>
</ResourceDictionary>
```

- [ ] **Step 7: Merge resource dictionaries in App.xaml**

Replace `src/ListaryOpen.App/App.xaml` with:

```xml
<Application x:Class="ListaryOpen.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="Styles/Colors.xaml" />
                <ResourceDictionary Source="Styles/Typography.xaml" />
                <ResourceDictionary Source="Styles/Controls.xaml" />
                <ResourceDictionary Source="Styles/SearchPanel.xaml" />
                <ResourceDictionary Source="Styles/Settings.xaml" />
            </ResourceDictionary.MergedDictionaries>

            <BitmapImage x:Key="ListaryOpenIcon"
                         UriSource="pack://application:,,,/Assets/ListaryOpen.ico" />
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 8: Name the existing settings root for smoke testing**

In `src/ListaryOpen.App/MainWindow.xaml`, change the root content grid from:

```xml
    <Grid Margin="20">
```

to:

```xml
    <Grid x:Name="SettingsContentRoot"
          Margin="20">
```

- [ ] **Step 9: Run the WPF smoke test**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter WpfSmokeTests -p:UseAppHost=false -p:BaseOutputPath=.test-ui-smoke\
```

Expected: PASS.

- [ ] **Step 10: Commit the resource foundation**

Run:

```powershell
git add -- src/ListaryOpen.App/App.xaml src/ListaryOpen.App/MainWindow.xaml src/ListaryOpen.App/Styles tests/ListaryOpen.Infrastructure.Tests/App/WpfSmokeTests.cs
git commit -m "test: add WPF UI resource smoke coverage"
```

## Task 2: Search Panel Presentation Properties

**Files:**
- Modify: `tests/ListaryOpen.Infrastructure.Tests/App/SearchPanelViewModelTests.cs`
- Modify: `src/ListaryOpen.App/ViewModels/SearchPanelViewModel.cs`

- [ ] **Step 1: Add failing tests for search presentation mode**

Append these tests to `tests/ListaryOpen.Infrastructure.Tests/App/SearchPanelViewModelTests.cs` before `CreateCandidate`:

```csharp
    [Fact]
    public void ConstructorStartsWithSearchPresentation()
    {
        var viewModel = new SearchPanelViewModel(new RecordingSearchIndex(Array.Empty<SearchResult>()));

        Assert.Equal("Search", viewModel.ModeDisplayText);
        Assert.Equal("Search files and folders", viewModel.QueryPlaceholderText);
    }

    [Fact]
    public async Task ActivateFolderSearchAsyncUsesDialogJumpPresentation()
    {
        var viewModel = new SearchPanelViewModel(new RecordingSearchIndex(Array.Empty<SearchResult>()));

        await viewModel.ActivateFolderSearchAsync(null);

        Assert.Equal("Dialog Jump", viewModel.ModeDisplayText);
        Assert.Equal("Jump dialog to folder", viewModel.QueryPlaceholderText);
    }

    [Fact]
    public async Task ActivateQuickSwitchFolderSearchAsyncUsesQuickSwitchPresentation()
    {
        var viewModel = new SearchPanelViewModel(
            new RecordingSearchIndex(Array.Empty<SearchResult>()),
            NormalizeTestFolder,
            _ => true,
            _ => DateTimeOffset.UtcNow);

        await viewModel.ActivateQuickSwitchFolderSearchAsync(new[]
        {
            CreateCandidate("C:\\Projects")
        });

        Assert.Equal("Quick Switch", viewModel.ModeDisplayText);
        Assert.Equal("Quick switch to folder", viewModel.QueryPlaceholderText);
    }

    [Fact]
    public async Task PresentationModeChangesRaisePropertyChanged()
    {
        var viewModel = new SearchPanelViewModel(new RecordingSearchIndex(Array.Empty<SearchResult>()));
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        await viewModel.ActivateFolderSearchAsync(null);

        Assert.Contains(nameof(SearchPanelViewModel.ModeDisplayText), changedProperties);
        Assert.Contains(nameof(SearchPanelViewModel.QueryPlaceholderText), changedProperties);
    }
```

- [ ] **Step 2: Run the failing tests**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~SearchPanelViewModelTests" -p:UseAppHost=false -p:BaseOutputPath=.test-ui-search-vm\
```

Expected: FAIL because `ModeDisplayText` and `QueryPlaceholderText` do not exist.

- [ ] **Step 3: Add search presentation state to SearchPanelViewModel**

In `src/ListaryOpen.App/ViewModels/SearchPanelViewModel.cs`, add this field after `_searchMode`:

```csharp
    private SearchPanelPresentationMode _presentationMode = SearchPanelPresentationMode.Search;
```

Add these properties after `StatusText`:

```csharp
    public string ModeDisplayText => _presentationMode switch
    {
        SearchPanelPresentationMode.Search => "Search",
        SearchPanelPresentationMode.DialogJump => "Dialog Jump",
        SearchPanelPresentationMode.QuickSwitch => "Quick Switch",
        _ => "Search"
    };

    public string QueryPlaceholderText => _presentationMode switch
    {
        SearchPanelPresentationMode.Search => "Search files and folders",
        SearchPanelPresentationMode.DialogJump => "Jump dialog to folder",
        SearchPanelPresentationMode.QuickSwitch => "Quick switch to folder",
        _ => "Search files and folders"
    };
```

At the start of `ActivateFilesAndFoldersSearchAsync`, before setting `_searchMode`, add:

```csharp
        SetPresentationMode(SearchPanelPresentationMode.Search);
```

At the start of `ActivateFolderSearchAsync`, before setting `_searchMode`, add:

```csharp
        SetPresentationMode(SearchPanelPresentationMode.DialogJump);
```

At the start of `ActivateQuickSwitchFolderSearchAsync`, after `ArgumentNullException.ThrowIfNull(candidates);`, add:

```csharp
        SetPresentationMode(SearchPanelPresentationMode.QuickSwitch);
```

Add this helper before `OnPropertyChanged`:

```csharp
    private void SetPresentationMode(SearchPanelPresentationMode presentationMode)
    {
        if (_presentationMode == presentationMode)
        {
            return;
        }

        _presentationMode = presentationMode;
        OnPropertyChanged(nameof(ModeDisplayText));
        OnPropertyChanged(nameof(QueryPlaceholderText));
    }
```

Add this enum after `SearchPanelViewModel`:

```csharp
internal enum SearchPanelPresentationMode
{
    Search,
    DialogJump,
    QuickSwitch
}
```

- [ ] **Step 4: Run the search ViewModel tests**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~SearchPanelViewModelTests" -p:UseAppHost=false -p:BaseOutputPath=.test-ui-search-vm\
```

Expected: PASS.

- [ ] **Step 5: Commit search presentation state**

Run:

```powershell
git add -- src/ListaryOpen.App/ViewModels/SearchPanelViewModel.cs tests/ListaryOpen.Infrastructure.Tests/App/SearchPanelViewModelTests.cs
git commit -m "feat: expose search panel presentation state"
```

## Task 3: Command-Palette Search Panel

**Files:**
- Create: `tests/ListaryOpen.Infrastructure.Tests/App/SearchPanelXamlTests.cs`
- Modify: `src/ListaryOpen.App/SearchPanel.xaml`
- Modify: `src/ListaryOpen.App/Styles/SearchPanel.xaml`

- [ ] **Step 1: Write failing XAML tests for the richer search panel**

Create `tests/ListaryOpen.Infrastructure.Tests/App/SearchPanelXamlTests.cs`:

```csharp
namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class SearchPanelXamlTests
{
    [Fact]
    public void SearchPanelBindsModePlaceholderAndRicherResultFields()
    {
        var xaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "SearchPanel.xaml"));

        Assert.Contains("x:Name=\"SearchPanelRoot\"", xaml);
        Assert.Contains("Text=\"{Binding ModeDisplayText}\"", xaml);
        Assert.Contains("Text=\"{Binding QueryPlaceholderText}\"", xaml);
        Assert.Contains("Text=\"{Binding Record.Name}\"", xaml);
        Assert.Contains("Text=\"{Binding Record.ParentPath}\"", xaml);
        Assert.Contains("Text=\"{Binding MatchReason}\"", xaml);
        Assert.Contains("Record.IsDirectory", xaml);
        Assert.Contains("SearchPanelResultListStyle", xaml);
    }

    private static string GetRepositoryPath(params string[] segments)
    {
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            Path.Combine(segments)));
    }
}
```

- [ ] **Step 2: Run the failing XAML tests**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter SearchPanelXamlTests -p:UseAppHost=false -p:BaseOutputPath=.test-ui-search-xaml\
```

Expected: FAIL because the current search panel does not bind to mode, placeholder, name, parent path, match reason, or directory type.

- [ ] **Step 3: Replace the search-panel styles**

Replace `src/ListaryOpen.App/Styles/SearchPanel.xaml` with:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Style x:Key="SearchPanelModeTextStyle" BasedOn="{StaticResource Text.SectionTitle}" TargetType="TextBlock">
        <Setter Property="FontSize" Value="13" />
        <Setter Property="Foreground" Value="{StaticResource Brush.Accent}" />
    </Style>

    <Style x:Key="SearchQueryBoxStyle" TargetType="TextBox">
        <Setter Property="Height" Value="48" />
        <Setter Property="Padding" Value="16,8" />
        <Setter Property="FontFamily" Value="{StaticResource Font.Primary}" />
        <Setter Property="FontSize" Value="{StaticResource FontSize.SearchQuery}" />
        <Setter Property="Foreground" Value="{StaticResource Brush.TextPrimary}" />
        <Setter Property="Background" Value="{StaticResource Brush.Surface}" />
        <Setter Property="BorderBrush" Value="{StaticResource Brush.Border}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="VerticalContentAlignment" Value="Center" />
    </Style>

    <Style x:Key="SearchPanelResultListStyle" TargetType="ListBox">
        <Setter Property="Background" Value="{StaticResource Brush.Surface}" />
        <Setter Property="BorderBrush" Value="{StaticResource Brush.Border}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled" />
        <Setter Property="ScrollViewer.VerticalScrollBarVisibility" Value="Auto" />
    </Style>

    <Style x:Key="SearchResultItemContainerStyle" TargetType="ListBoxItem">
        <Setter Property="MinHeight" Value="58" />
        <Setter Property="Padding" Value="0" />
        <Setter Property="HorizontalContentAlignment" Value="Stretch" />
        <Setter Property="BorderThickness" Value="0" />
        <Style.Triggers>
            <Trigger Property="IsSelected" Value="True">
                <Setter Property="Background" Value="{StaticResource Brush.Selection}" />
            </Trigger>
        </Style.Triggers>
    </Style>

    <Style x:Key="SearchResultTitleStyle" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource Font.Primary}" />
        <Setter Property="FontSize" Value="{StaticResource FontSize.SearchTitle}" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="Foreground" Value="{StaticResource Brush.TextPrimary}" />
        <Setter Property="TextTrimming" Value="CharacterEllipsis" />
        <Setter Property="TextWrapping" Value="NoWrap" />
    </Style>

    <Style x:Key="SearchResultPathStyle" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource Font.Primary}" />
        <Setter Property="FontSize" Value="{StaticResource FontSize.Small}" />
        <Setter Property="Foreground" Value="{StaticResource Brush.TextMuted}" />
        <Setter Property="TextTrimming" Value="CharacterEllipsis" />
        <Setter Property="TextWrapping" Value="NoWrap" />
    </Style>

    <Style x:Key="SearchStatusTextStyle" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource Font.Primary}" />
        <Setter Property="FontSize" Value="{StaticResource FontSize.Small}" />
        <Setter Property="Foreground" Value="{StaticResource Brush.TextMuted}" />
        <Setter Property="TextWrapping" Value="Wrap" />
        <Setter Property="TextTrimming" Value="CharacterEllipsis" />
        <Setter Property="MaxHeight" Value="42" />
    </Style>
</ResourceDictionary>
```

- [ ] **Step 4: Replace SearchPanel.xaml with command-palette layout**

Replace `src/ListaryOpen.App/SearchPanel.xaml` with:

```xml
<Window x:Class="ListaryOpen.App.SearchPanel"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="ListaryOpen Search"
        Icon="{StaticResource ListaryOpenIcon}"
        Width="760"
        Height="460"
        MinWidth="620"
        MinHeight="360"
        Background="{StaticResource Brush.AppBackground}"
        WindowStartupLocation="CenterScreen"
        Topmost="True">
    <Grid x:Name="SearchPanelRoot"
          Margin="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <Grid Margin="0,0,0,10">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>

            <TextBlock Style="{StaticResource SearchPanelModeTextStyle}"
                       Text="{Binding ModeDisplayText}"
                       VerticalAlignment="Center" />

            <TextBlock Grid.Column="1"
                       Style="{StaticResource Text.Body}"
                       Text="{Binding StatusText}"
                       TextTrimming="CharacterEllipsis"
                       MaxWidth="260"
                       VerticalAlignment="Center" />
        </Grid>

        <Grid Grid.Row="1"
              Margin="0,0,0,12">
            <TextBox x:Name="QueryBox"
                     Style="{StaticResource SearchQueryBoxStyle}"
                     AutomationProperties.Name="Search query"
                     Text="{Binding QueryText, UpdateSourceTrigger=PropertyChanged}" />

            <TextBlock Text="{Binding QueryPlaceholderText}"
                       Foreground="{StaticResource Brush.TextMuted}"
                       FontFamily="{StaticResource Font.Primary}"
                       FontSize="{StaticResource FontSize.Body}"
                       Margin="18,0,0,0"
                       VerticalAlignment="Center"
                       IsHitTestVisible="False">
                <TextBlock.Style>
                    <Style TargetType="TextBlock">
                        <Setter Property="Visibility" Value="Collapsed" />
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding Text, ElementName=QueryBox}" Value="">
                                <Setter Property="Visibility" Value="Visible" />
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </TextBlock.Style>
            </TextBlock>
        </Grid>

        <ListBox x:Name="ResultsList"
                 Grid.Row="2"
                 Style="{StaticResource SearchPanelResultListStyle}"
                 ItemContainerStyle="{StaticResource SearchResultItemContainerStyle}"
                 ItemsSource="{Binding Results}"
                 MouseDoubleClick="ResultsList_MouseDoubleClick"
                 AutomationProperties.Name="Search results"
                 SelectedItem="{Binding SelectedResult, Mode=TwoWay}">
            <ListBox.ItemTemplate>
                <DataTemplate>
                    <Grid Margin="12,8">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="28" />
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>

                        <TextBlock FontFamily="Segoe MDL2 Assets"
                                   FontSize="16"
                                   Foreground="{StaticResource Brush.TextSecondary}"
                                   Text="&#xE8A5;"
                                   VerticalAlignment="Center">
                            <TextBlock.Style>
                                <Style TargetType="TextBlock">
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding Record.IsDirectory}" Value="True">
                                            <Setter Property="Text" Value="&#xE8B7;" />
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </TextBlock.Style>
                        </TextBlock>

                        <StackPanel Grid.Column="1"
                                    Margin="4,0,12,0">
                            <TextBlock Style="{StaticResource SearchResultTitleStyle}"
                                       Text="{Binding Record.Name}" />
                            <TextBlock Style="{StaticResource SearchResultPathStyle}"
                                       Text="{Binding Record.ParentPath}" />
                        </StackPanel>

                        <StackPanel Grid.Column="2"
                                    Orientation="Horizontal"
                                    VerticalAlignment="Center">
                            <TextBlock Text="File"
                                       Margin="0,0,6,0">
                                <TextBlock.Style>
                                    <Style BasedOn="{StaticResource BadgeTextBlockStyle}" TargetType="TextBlock">
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding Record.IsDirectory}" Value="True">
                                                <Setter Property="Text" Value="Folder" />
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </TextBlock.Style>
                            </TextBlock>
                            <TextBlock Style="{StaticResource BadgeTextBlockStyle}"
                                       Text="{Binding MatchReason}" />
                        </StackPanel>
                    </Grid>
                </DataTemplate>
            </ListBox.ItemTemplate>
        </ListBox>

        <TextBlock x:Name="SearchStatusText"
                   Grid.Row="3"
                   Margin="2,8,2,0"
                   MinHeight="18"
                   Style="{StaticResource SearchStatusTextStyle}"
                   AutomationProperties.Name="Search status"
                   Text="{Binding StatusText}" />
    </Grid>
</Window>
```

- [ ] **Step 5: Run search XAML and smoke tests**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter "SearchPanelXamlTests|WpfSmokeTests|SearchPanelKeyboardTests" -p:UseAppHost=false -p:BaseOutputPath=.test-ui-search-xaml\
```

Expected: PASS.

- [ ] **Step 6: Commit the search panel UI**

Run:

```powershell
git add -- src/ListaryOpen.App/SearchPanel.xaml src/ListaryOpen.App/Styles/SearchPanel.xaml tests/ListaryOpen.Infrastructure.Tests/App/SearchPanelXamlTests.cs
git commit -m "feat: modernize search panel layout"
```

## Task 4: Settings Presentation Properties

**Files:**
- Modify: `tests/ListaryOpen.Infrastructure.Tests/App/SettingsViewModelTests.cs`
- Modify: `tests/ListaryOpen.Infrastructure.Tests/App/SettingsHookQuickSwitchTests.cs`
- Modify: `src/ListaryOpen.App/ViewModels/SettingsViewModel.cs`

- [ ] **Step 1: Add failing settings presentation tests**

Append these tests to `tests/ListaryOpen.Infrastructure.Tests/App/SettingsViewModelTests.cs`:

```csharp
    [Fact]
    public void ConstructorStartsWithPresentationBadges()
    {
        var viewModel = new SettingsViewModel(AppSettings.Defaults());

        Assert.Equal("Idle", viewModel.IndexingBadgeText);
        Assert.Equal("Disabled", viewModel.NtfsFastIndexingBadgeText);
        Assert.Equal("Enable", viewModel.NtfsFastIndexingActionText);
        Assert.Equal("Enabled", viewModel.QuickSaveOpenBadgeText);
    }

    [Fact]
    public void UpdateIndexingStatusUpdatesPresentationBadge()
    {
        var viewModel = new SettingsViewModel(AppSettings.Defaults());
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        viewModel.UpdateIndexingStatus(new IndexingStatus(IndexingRunState.Completed, "Indexing completed.", 42));

        Assert.Equal("Completed", viewModel.IndexingBadgeText);
        Assert.Contains(nameof(SettingsViewModel.IndexingBadgeText), changedProperties);
    }

    [Fact]
    public void EnableNtfsFastIndexingCommandUpdatesPresentationBadges()
    {
        var viewModel = new SettingsViewModel(AppSettings.Defaults(), () => { });
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        viewModel.EnableNtfsFastIndexingCommand.Execute(null);

        Assert.Equal("Enabled", viewModel.NtfsFastIndexingBadgeText);
        Assert.Equal("Enabled", viewModel.NtfsFastIndexingActionText);
        Assert.Contains(nameof(SettingsViewModel.NtfsFastIndexingBadgeText), changedProperties);
        Assert.Contains(nameof(SettingsViewModel.NtfsFastIndexingActionText), changedProperties);
    }
```

Append these tests to `tests/ListaryOpen.Infrastructure.Tests/App/SettingsHookQuickSwitchTests.cs` before `CreatePartialStatus`:

```csharp
    [Fact]
    public void HookPresentationStartsDisabled()
    {
        var viewModel = new SettingsViewModel(AppSettings.Defaults());

        Assert.Equal("Disabled", viewModel.HookQuickSwitchBadgeText);
        Assert.Equal("Enable", viewModel.HookQuickSwitchActionText);
    }

    [Fact]
    public void EnableHookCommandShowsEnablingActionText()
    {
        var viewModel = new SettingsViewModel(
            AppSettings.Defaults(),
            enableNtfsFastIndexing: null,
            enableHookQuickSwitch: () => { });

        viewModel.EnableHookQuickSwitchCommand.Execute(null);

        Assert.Equal("Enabling", viewModel.HookQuickSwitchActionText);
    }

    [Fact]
    public void HookPresentationShowsReadyWhenBothHostsRun()
    {
        var viewModel = new SettingsViewModel(AppSettings.Defaults());

        viewModel.UpdateHookQuickSwitchStatus(new HookQuickSwitchStatus(
            true,
            new HookArchitectureStatus(HookArchitecture.X64, true, true, true, "x64 running"),
            new HookArchitectureStatus(HookArchitecture.X86, true, true, true, "x86 running")));

        Assert.Equal("Ready", viewModel.HookQuickSwitchBadgeText);
        Assert.Equal("Enabled", viewModel.HookQuickSwitchActionText);
    }

    [Fact]
    public void HookPresentationShowsDegradedWhenOnlyOneHostRuns()
    {
        var viewModel = new SettingsViewModel(AppSettings.Defaults());

        viewModel.UpdateHookQuickSwitchStatus(CreatePartialStatus());

        Assert.Equal("Degraded", viewModel.HookQuickSwitchBadgeText);
        Assert.Equal("Retry", viewModel.HookQuickSwitchActionText);
    }
```

- [ ] **Step 2: Run the failing settings tests**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter "SettingsViewModelTests|SettingsHookQuickSwitchTests" -p:UseAppHost=false -p:BaseOutputPath=.test-ui-settings-vm\
```

Expected: FAIL because the presentation badge/action properties do not exist.

- [ ] **Step 3: Add settings presentation properties**

In `src/ListaryOpen.App/ViewModels/SettingsViewModel.cs`, add this field after `_hookQuickSwitchStatus`:

```csharp
    private IndexingRunState _indexingState = IndexingRunState.Idle;
```

Add these properties after `IndexingStatusText`:

```csharp
    public string IndexingBadgeText => _indexingState switch
    {
        IndexingRunState.Idle => "Idle",
        IndexingRunState.Indexing => "Indexing",
        IndexingRunState.Completed => "Completed",
        IndexingRunState.Failed => "Failed",
        _ => "Idle"
    };

    public string NtfsFastIndexingBadgeText => NtfsFastIndexingEnabled ? "Enabled" : "Disabled";

    public string NtfsFastIndexingActionText => NtfsFastIndexingEnabled ? "Enabled" : "Enable";

    public string HookQuickSwitchBadgeText
    {
        get
        {
            if (!_hookQuickSwitchStatus.Enabled)
            {
                return "Disabled";
            }

            if (_hookQuickSwitchStatus.X64.HostRunning && _hookQuickSwitchStatus.X86.HostRunning)
            {
                return "Ready";
            }

            if (_hookQuickSwitchStatus.X64.HostRunning || _hookQuickSwitchStatus.X86.HostRunning)
            {
                return "Degraded";
            }

            return "Failed";
        }
    }

    public string HookQuickSwitchActionText
    {
        get
        {
            if (_hookQuickSwitchEnableInProgress)
            {
                return "Enabling";
            }

            if (!_hookQuickSwitchStatus.Enabled)
            {
                return "Enable";
            }

            return HookQuickSwitchBadgeText == "Ready" ? "Enabled" : "Retry";
        }
    }

    public string QuickSaveOpenBadgeText => Settings.QuickSaveOpenEnabled ? "Enabled" : "Disabled";
```

In `NtfsFastIndexingEnabled` setter, after `OnPropertyChanged(nameof(NtfsFastIndexingStatusText));`, add:

```csharp
            OnPropertyChanged(nameof(NtfsFastIndexingBadgeText));
            OnPropertyChanged(nameof(NtfsFastIndexingActionText));
```

Replace `UpdateIndexingStatus` with:

```csharp
    public void UpdateIndexingStatus(IndexingStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        var indexingStateChanged = _indexingState != status.State;
        _indexingState = status.State;
        IndexingStatusText = $"Indexing: {status.Message} ({status.IndexedCount})";

        if (indexingStateChanged)
        {
            OnPropertyChanged(nameof(IndexingBadgeText));
        }
    }
```

In `UpdateHookQuickSwitchStatus`, inside the `if (!Equals(...))` block after `OnPropertyChanged(nameof(HookQuickSwitchStatusText));`, add:

```csharp
            OnPropertyChanged(nameof(HookQuickSwitchBadgeText));
            OnPropertyChanged(nameof(HookQuickSwitchActionText));
```

In `SetHookQuickSwitchEnableInProgress`, after `_hookQuickSwitchEnableInProgress = value;`, add:

```csharp
        OnPropertyChanged(nameof(HookQuickSwitchActionText));
```

- [ ] **Step 4: Run settings ViewModel tests**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter "SettingsViewModelTests|SettingsHookQuickSwitchTests" -p:UseAppHost=false -p:BaseOutputPath=.test-ui-settings-vm\
```

Expected: PASS.

- [ ] **Step 5: Commit settings presentation properties**

Run:

```powershell
git add -- src/ListaryOpen.App/ViewModels/SettingsViewModel.cs tests/ListaryOpen.Infrastructure.Tests/App/SettingsViewModelTests.cs tests/ListaryOpen.Infrastructure.Tests/App/SettingsHookQuickSwitchTests.cs
git commit -m "feat: expose settings presentation state"
```

## Task 5: Grouped Settings Window

**Files:**
- Create: `tests/ListaryOpen.Infrastructure.Tests/App/SettingsXamlTests.cs`
- Modify: `src/ListaryOpen.App/MainWindow.xaml`
- Modify: `src/ListaryOpen.App/Styles/Settings.xaml`

- [ ] **Step 1: Write failing settings XAML tests**

Create `tests/ListaryOpen.Infrastructure.Tests/App/SettingsXamlTests.cs`:

```csharp
namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class SettingsXamlTests
{
    [Fact]
    public void SettingsWindowDefinesOperationalSectionsAndPresentationBindings()
    {
        var xaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "MainWindow.xaml"));

        Assert.Contains("x:Name=\"IndexingSection\"", xaml);
        Assert.Contains("x:Name=\"HotkeysSection\"", xaml);
        Assert.Contains("x:Name=\"QuickSwitchSection\"", xaml);
        Assert.Contains("x:Name=\"GeneralSection\"", xaml);
        Assert.Contains("IndexingBadgeText", xaml);
        Assert.Contains("NtfsFastIndexingBadgeText", xaml);
        Assert.Contains("NtfsFastIndexingActionText", xaml);
        Assert.Contains("HookQuickSwitchBadgeText", xaml);
        Assert.Contains("HookQuickSwitchActionText", xaml);
        Assert.Contains("QuickSaveOpenBadgeText", xaml);
    }

    private static string GetRepositoryPath(params string[] segments)
    {
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            Path.Combine(segments)));
    }
}
```

- [ ] **Step 2: Run the failing settings XAML test**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter SettingsXamlTests -p:UseAppHost=false -p:BaseOutputPath=.test-ui-settings-xaml\
```

Expected: FAIL because the current settings window does not define the named sections or presentation bindings.

- [ ] **Step 3: Replace settings styles**

Replace `src/ListaryOpen.App/Styles/Settings.xaml` with:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Style x:Key="SettingsSectionStyle" TargetType="Border">
        <Setter Property="Background" Value="{StaticResource Brush.Surface}" />
        <Setter Property="BorderBrush" Value="{StaticResource Brush.Border}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="CornerRadius" Value="{StaticResource Radius.Section}" />
        <Setter Property="Padding" Value="{StaticResource Thickness.SectionPadding}" />
        <Setter Property="Margin" Value="0,0,0,12" />
    </Style>

    <Style x:Key="SettingsRowTitleStyle" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource Font.Primary}" />
        <Setter Property="FontSize" Value="{StaticResource FontSize.Body}" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="Foreground" Value="{StaticResource Brush.TextPrimary}" />
        <Setter Property="TextTrimming" Value="CharacterEllipsis" />
    </Style>

    <Style x:Key="SettingsDetailStyle" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource Font.Primary}" />
        <Setter Property="FontSize" Value="{StaticResource FontSize.Small}" />
        <Setter Property="Foreground" Value="{StaticResource Brush.TextMuted}" />
        <Setter Property="TextWrapping" Value="Wrap" />
        <Setter Property="TextTrimming" Value="CharacterEllipsis" />
        <Setter Property="MaxHeight" Value="40" />
    </Style>

    <Style x:Key="SettingsPathTextStyle" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="Cascadia Mono, Consolas" />
        <Setter Property="FontSize" Value="{StaticResource FontSize.Small}" />
        <Setter Property="Foreground" Value="{StaticResource Brush.TextSecondary}" />
        <Setter Property="TextTrimming" Value="CharacterEllipsis" />
        <Setter Property="TextWrapping" Value="NoWrap" />
        <Setter Property="Margin" Value="0,2,0,2" />
    </Style>
</ResourceDictionary>
```

- [ ] **Step 4: Replace MainWindow.xaml with grouped settings layout**

Replace `src/ListaryOpen.App/MainWindow.xaml` with:

```xml
<Window x:Class="ListaryOpen.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
        xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
        mc:Ignorable="d"
        Title="ListaryOpen Settings"
        Icon="{StaticResource ListaryOpenIcon}"
        Height="520"
        Width="720"
        MinHeight="420"
        MinWidth="560"
        Background="{StaticResource Brush.AppBackground}"
        WindowStartupLocation="CenterScreen">
    <Grid x:Name="SettingsContentRoot"
          Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>

        <TextBlock Style="{StaticResource Text.WindowTitle}"
                   Text="ListaryOpen Settings"
                   Margin="0,0,0,16" />

        <ScrollViewer Grid.Row="1"
                      VerticalScrollBarVisibility="Auto"
                      HorizontalScrollBarVisibility="Disabled">
            <StackPanel>
                <Border x:Name="IndexingSection"
                        Style="{StaticResource SettingsSectionStyle}">
                    <StackPanel>
                        <Grid Margin="0,0,0,12">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <TextBlock Style="{StaticResource Text.SectionTitle}"
                                       Text="Indexing" />
                            <TextBlock Grid.Column="1"
                                       Style="{StaticResource BadgeTextBlockStyle}"
                                       Text="{Binding IndexingBadgeText}" />
                        </Grid>

                        <TextBlock Style="{StaticResource SettingsDetailStyle}"
                                   AutomationProperties.Name="Indexing status"
                                   Text="{Binding IndexingStatusText}" />

                        <TextBlock Style="{StaticResource SettingsRowTitleStyle}"
                                   Text="Indexed roots"
                                   Margin="0,14,0,6" />
                        <ItemsControl ItemsSource="{Binding Settings.IndexedRoots}">
                            <ItemsControl.ItemTemplate>
                                <DataTemplate>
                                    <TextBlock Style="{StaticResource SettingsPathTextStyle}"
                                               Text="{Binding}" />
                                </DataTemplate>
                            </ItemsControl.ItemTemplate>
                        </ItemsControl>

                        <Grid Margin="0,14,0,0">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <StackPanel>
                                <TextBlock Style="{StaticResource SettingsRowTitleStyle}"
                                           Text="NTFS fast indexing" />
                                <TextBlock Style="{StaticResource SettingsDetailStyle}"
                                           Text="{Binding NtfsFastIndexingStatusText}" />
                            </StackPanel>
                            <TextBlock Grid.Column="1"
                                       Style="{StaticResource BadgeTextBlockStyle}"
                                       Text="{Binding NtfsFastIndexingBadgeText}"
                                       Margin="12,0,8,0"
                                       VerticalAlignment="Center" />
                            <Button Grid.Column="2"
                                    Style="{StaticResource ActionButtonStyle}"
                                    Content="{Binding NtfsFastIndexingActionText}"
                                    Command="{Binding EnableNtfsFastIndexingCommand}"
                                    VerticalAlignment="Center" />
                        </Grid>
                    </StackPanel>
                </Border>

                <Border x:Name="HotkeysSection"
                        Style="{StaticResource SettingsSectionStyle}">
                    <StackPanel>
                        <TextBlock Style="{StaticResource Text.SectionTitle}"
                                   Text="Hotkeys"
                                   Margin="0,0,0,12" />
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="*" />
                            </Grid.ColumnDefinitions>
                            <StackPanel Margin="0,0,12,0">
                                <TextBlock Style="{StaticResource SettingsRowTitleStyle}"
                                           Text="Search" />
                                <TextBlock Style="{StaticResource SettingsDetailStyle}"
                                           Text="{Binding Settings.SearchHotkey}" />
                            </StackPanel>
                            <StackPanel Grid.Column="1">
                                <TextBlock Style="{StaticResource SettingsRowTitleStyle}"
                                           Text="Dialog jump" />
                                <TextBlock Style="{StaticResource SettingsDetailStyle}"
                                           Text="{Binding Settings.DialogHotkey}" />
                            </StackPanel>
                        </Grid>
                    </StackPanel>
                </Border>

                <Border x:Name="QuickSwitchSection"
                        Style="{StaticResource SettingsSectionStyle}">
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="Auto" />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        <StackPanel>
                            <TextBlock Style="{StaticResource Text.SectionTitle}"
                                       Text="Quick Switch"
                                       Margin="0,0,0,8" />
                            <TextBlock Style="{StaticResource SettingsDetailStyle}"
                                       Text="{Binding HookQuickSwitchStatusText}" />
                        </StackPanel>
                        <TextBlock Grid.Column="1"
                                   Style="{StaticResource BadgeTextBlockStyle}"
                                   Text="{Binding HookQuickSwitchBadgeText}"
                                   Margin="12,0,8,0"
                                   VerticalAlignment="Center" />
                        <Button Grid.Column="2"
                                Style="{StaticResource ActionButtonStyle}"
                                Content="{Binding HookQuickSwitchActionText}"
                                Command="{Binding EnableHookQuickSwitchCommand}"
                                VerticalAlignment="Center" />
                    </Grid>
                </Border>

                <Border x:Name="GeneralSection"
                        Style="{StaticResource SettingsSectionStyle}">
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        <StackPanel>
                            <TextBlock Style="{StaticResource Text.SectionTitle}"
                                       Text="General"
                                       Margin="0,0,0,8" />
                            <TextBlock Style="{StaticResource SettingsRowTitleStyle}"
                                       Text="Quick Save / Open" />
                            <TextBlock Style="{StaticResource SettingsDetailStyle}"
                                       Text="Current configured state for Quick Save and Open integration." />
                        </StackPanel>
                        <TextBlock Grid.Column="1"
                                   Style="{StaticResource BadgeTextBlockStyle}"
                                   Text="{Binding QuickSaveOpenBadgeText}"
                                   Margin="12,0,0,0"
                                   VerticalAlignment="Center" />
                    </Grid>
                </Border>
            </StackPanel>
        </ScrollViewer>
    </Grid>
</Window>
```

- [ ] **Step 5: Run settings XAML and smoke tests**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter "SettingsXamlTests|WpfSmokeTests|AppIconTests" -p:UseAppHost=false -p:BaseOutputPath=.test-ui-settings-xaml\
```

Expected: PASS.

- [ ] **Step 6: Commit grouped settings UI**

Run:

```powershell
git add -- src/ListaryOpen.App/MainWindow.xaml src/ListaryOpen.App/Styles/Settings.xaml tests/ListaryOpen.Infrastructure.Tests/App/SettingsXamlTests.cs
git commit -m "feat: modernize settings window layout"
```

## Task 6: Manual Checklist And Final Verification

**Files:**
- Modify: `docs/manual-test-checklist.md`

- [ ] **Step 1: Add UI modernization manual checks**

In `docs/manual-test-checklist.md`, add this section after the automated tests section:

```markdown
- [ ] Verify modernized search panel UI.
  - Press Ctrl+Space and confirm the panel opens as a compact command palette.
  - Confirm the top mode label reads Search for normal file/folder search.
  - Type a known query and confirm result rows show name, parent path, file/folder badge, and match reason.
  - Confirm long names and parent paths trim without overlapping badges.
  - Press Enter, Esc, Ctrl+Enter, and Ctrl+C and confirm existing behavior is unchanged.
- [ ] Verify dialog/quick-switch search panel mode UI.
  - Press Ctrl+G over a supported dialog and confirm the panel shows Dialog Jump or Quick Switch mode.
  - Confirm folder-only results remain readable and keyboard focus stays in the query box.
  - Confirm long dialog jump status messages stay within the status area.
- [ ] Verify modernized settings UI.
  - Open settings from the tray and confirm Indexing, Hotkeys, Quick Switch, and General sections are visible.
  - Confirm indexed roots, NTFS fast indexing, hook quick switch, and Quick Save/Open states use readable badges.
  - Resize the settings window to its minimum size and confirm text remains readable without overlap.
  - Check 100%, 125%, and 150% DPI if available on the test machine.
```

- [ ] **Step 2: Run app-focused tests**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~ListaryOpen.Infrastructure.Tests.App" -p:UseAppHost=false -p:BaseOutputPath=.test-ui-app-final\
```

Expected: PASS.

- [ ] **Step 3: Run the full managed test suite**

Run:

```powershell
dotnet test ListaryOpen.sln --no-restore --nologo -p:UseAppHost=false -p:BaseOutputPath=.test-ui-solution-final\
```

Expected: PASS. If this fails because restore artifacts are missing, run `dotnet restore ListaryOpen.sln` and rerun the same test command.

- [ ] **Step 4: Build the WPF app**

Run:

```powershell
dotnet build src\ListaryOpen.App\ListaryOpen.App.csproj --no-restore --nologo -p:UseAppHost=false -p:BaseOutputPath=.test-ui-app-build\
```

Expected: PASS.

- [ ] **Step 5: Manual UI smoke**

Run the app from the build output that matches the current configuration, then complete the manual checklist section added in Step 1. Record the result under each checklist item using `PASS`, `PARTIAL`, `NOT VERIFIED`, or `FAIL` with one short reason.

- [ ] **Step 6: Commit manual checklist and verification notes**

Run:

```powershell
git add -- docs/manual-test-checklist.md
git commit -m "docs: add UI modernization acceptance checks"
```

## Final Acceptance

After all tasks are complete:

1. Run `git status --short` and confirm only unrelated pre-existing UAC/hook/NTFS edits remain.
2. Run `git log --oneline -6` and confirm the UI modernization commits are present.
3. Confirm the app still uses WPF and no new UI package dependency was added.
4. Confirm no task touched hook IPC, search ranking, dialog automation, or indexing business logic outside the approved presentation properties.
