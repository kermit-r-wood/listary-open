using ListaryOpen.Core.Settings;
using ListaryOpen.Infrastructure.Hooks;
using ListaryOpen.Infrastructure.Indexing;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace ListaryOpen.App.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly Action _enableHookQuickSwitch;
    private readonly Action _enableNtfsFastIndexing;
    private bool _hookQuickSwitchEnableInProgress;
    private HookQuickSwitchStatus _hookQuickSwitchStatus = HookQuickSwitchStatus.Disabled();
    private IndexingRunState _indexingState = IndexingRunState.Idle;
    private string _indexingStatusText = "Indexing: idle";
    private bool _ntfsFastIndexingEnabled;

    public SettingsViewModel()
        : this(AppSettings.Defaults())
    {
    }

    public SettingsViewModel(AppSettings settings)
        : this(settings, null)
    {
    }

    public SettingsViewModel(AppSettings settings, Action? enableNtfsFastIndexing)
        : this(settings, enableNtfsFastIndexing, null)
    {
    }

    public SettingsViewModel(AppSettings settings, Action? enableNtfsFastIndexing, Action? enableHookQuickSwitch)
    {
        Settings = settings;
        _enableNtfsFastIndexing = enableNtfsFastIndexing ?? (() => { });
        _enableHookQuickSwitch = enableHookQuickSwitch ?? (() => { });
        EnableNtfsFastIndexingCommand = new RelayCommand(
            EnableNtfsFastIndexing,
            () => !NtfsFastIndexingEnabled);
        EnableHookQuickSwitchCommand = new RelayCommand(
            EnableHookQuickSwitch,
            () => !_hookQuickSwitchEnableInProgress);
    }

    public AppSettings Settings { get; }

    public ICommand EnableHookQuickSwitchCommand { get; }

    public ICommand EnableNtfsFastIndexingCommand { get; }

    public string HookQuickSwitchStatusText => _hookQuickSwitchStatus.DisplayText;

    public string IndexingStatusText
    {
        get => _indexingStatusText;
        private set
        {
            if (string.Equals(_indexingStatusText, value, StringComparison.Ordinal))
            {
                return;
            }

            _indexingStatusText = value;
            OnPropertyChanged();
        }
    }

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

            if (IsHookQuickSwitchReady)
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

            return IsHookQuickSwitchReady ? "Enabled" : "Retry";
        }
    }

    public string QuickSaveOpenBadgeText => Settings.QuickSaveOpenEnabled ? "Enabled" : "Disabled";

    public bool NtfsFastIndexingEnabled
    {
        get => _ntfsFastIndexingEnabled;
        private set
        {
            if (_ntfsFastIndexingEnabled == value)
            {
                return;
            }

            _ntfsFastIndexingEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NtfsFastIndexingStatusText));
            OnPropertyChanged(nameof(NtfsFastIndexingBadgeText));
            OnPropertyChanged(nameof(NtfsFastIndexingActionText));
            if (EnableNtfsFastIndexingCommand is RelayCommand command)
            {
                command.RaiseCanExecuteChanged();
            }
        }
    }

    public string NtfsFastIndexingStatusText => NtfsFastIndexingEnabled
        ? "NTFS fast indexing: enabled for this session"
        : "NTFS fast indexing: disabled";

    public event PropertyChangedEventHandler? PropertyChanged;

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

    public void UpdateHookQuickSwitchStatus(HookQuickSwitchStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (!Equals(_hookQuickSwitchStatus, status))
        {
            _hookQuickSwitchStatus = status;
            OnPropertyChanged(nameof(HookQuickSwitchStatusText));
            OnPropertyChanged(nameof(HookQuickSwitchBadgeText));
            if (!_hookQuickSwitchEnableInProgress)
            {
                OnPropertyChanged(nameof(HookQuickSwitchActionText));
            }
        }

        SetHookQuickSwitchEnableInProgress(false);
    }

    private void EnableHookQuickSwitch()
    {
        SetHookQuickSwitchEnableInProgress(true);
        try
        {
            _enableHookQuickSwitch();
        }
        catch
        {
            SetHookQuickSwitchEnableInProgress(false);
            throw;
        }
    }

    private void SetHookQuickSwitchEnableInProgress(bool value)
    {
        if (_hookQuickSwitchEnableInProgress == value)
        {
            return;
        }

        _hookQuickSwitchEnableInProgress = value;
        OnPropertyChanged(nameof(HookQuickSwitchActionText));
        if (EnableHookQuickSwitchCommand is RelayCommand command)
        {
            command.RaiseCanExecuteChanged();
        }
    }

    private void EnableNtfsFastIndexing()
    {
        if (NtfsFastIndexingEnabled)
        {
            return;
        }

        _enableNtfsFastIndexing();
        NtfsFastIndexingEnabled = true;
    }

    private bool IsHookQuickSwitchReady => _hookQuickSwitchStatus.Enabled
        && _hookQuickSwitchStatus.X64.HostRunning
        && _hookQuickSwitchStatus.X86.HostRunning;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool> _canExecute;

    public RelayCommand(Action execute, Func<bool> canExecute)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute();

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            _execute();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
