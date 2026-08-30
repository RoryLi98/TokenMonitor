using System.Windows;
using System.Windows.Threading;
using TokenMonitor.App.Infrastructure;
using TokenMonitor.App.ViewModels;
using TokenMonitor.Core.Services;
using Forms = System.Windows.Forms;

namespace TokenMonitor.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName =
        @"Local\TokenMonitor.7F4525C0-CA2D-43BD-9FF6-131414F11099";
    private const string ActivationEventName =
        @"Local\TokenMonitor.Activate.7F4525C0-CA2D-43BD-9FF6-131414F11099";

    private MainWindow? _window;
    private TaskbarBarWindow? _taskbarWindow;
    private MainViewModel? _viewModel;
    private Forms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _currentIcon;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;
    private bool _ownsSingleInstanceMutex;
    private bool _isExiting;
    private AppSettingsStore? _settingsStore;
    private AppSettings? _settings;
    private Forms.ToolStripMenuItem? _taskbarMenuItem;
    private readonly Dictionary<int, Forms.ToolStripMenuItem> _refreshIntervalMenuItems = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _activationEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.AutoReset,
            name: ActivationEventName);
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: SingleInstanceMutexName,
            createdNew: out _ownsSingleInstanceMutex);
        if (!_ownsSingleInstanceMutex)
        {
            _activationEvent.Set();
            _activationEvent.Dispose();
            _activationEvent = null;
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, timedOut) =>
            {
                if (!timedOut && !_isExiting)
                {
                    _ = Dispatcher.BeginInvoke(
                        ActivateExistingInstance,
                        DispatcherPriority.Normal);
                }
            },
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        var providers = new IQuotaProvider[]
        {
            new CodexQuotaProvider(),
            new ClaudeDesktopQuotaProvider(),
        };

        _viewModel = new MainViewModel(providers);
        _viewModel.SnapshotsChanged += OnSnapshotsChanged;

        _settingsStore = new AppSettingsStore();
        _settings = _settingsStore.Load();

        _window = new MainWindow(_viewModel);
        _window.SetRefreshInterval(TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds));
        _window.TaskbarRecreated += OnTaskbarRecreated;

        _window.ShowPopup();
        if (_settings.ShowTaskbarBar)
        {
            CreateTaskbarWindow();
            _taskbarWindow?.Show();
        }
        _ = Dispatcher.InvokeAsync(CreateTrayIcon, DispatcherPriority.ContextIdle);
    }

    private void CreateTaskbarWindow()
    {
        if (_taskbarWindow is not null ||
            _viewModel is null ||
            _settingsStore is null ||
            _settings is null)
        {
            return;
        }

        var taskbarWindow = new TaskbarBarWindow(
            _viewModel,
            new TaskbarPlacementService(),
            _settingsStore,
            _settings);
        taskbarWindow.OpenRequested += (_, _) => ShowWindow();
        taskbarWindow.RefreshRequested += (_, _) => _viewModel.RefreshCommand.Execute(null);
        taskbarWindow.SettingsRequested += (_, _) => ShowTaskbarSettings();
        taskbarWindow.HideRequested += (_, _) => SetTaskbarBarVisible(false);
        taskbarWindow.ExitRequested += OnExitRequested;
        _taskbarWindow = taskbarWindow;
    }

    private void CloseTaskbarWindow()
    {
        var taskbarWindow = _taskbarWindow;
        _taskbarWindow = null;
        try
        {
            taskbarWindow?.Close();
        }
        catch (InvalidOperationException)
        {
            // Explorer may already have destroyed the embedded child HWND.
        }
    }

    private async void OnTaskbarRecreated(object? sender, EventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        await Task.Delay(750);
        CloseTaskbarWindow();
        if (_settings?.ShowTaskbarBar == true)
        {
            CreateTaskbarWindow();
            _taskbarWindow?.Show();
        }

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Visible = true;
        }
    }

    private void CreateTrayIcon()
    {
        if (_viewModel is null)
        {
            return;
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 TokenMonitor", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add("立即刷新", null, (_, _) => Dispatcher.Invoke(
            () => _viewModel.RefreshCommand.Execute(null)));

        var refreshIntervalMenu = new Forms.ToolStripMenuItem("自动刷新频率");
        AddRefreshIntervalMenuItem(refreshIntervalMenu, "30 秒", 30);
        AddRefreshIntervalMenuItem(refreshIntervalMenu, "1 分钟", 60);
        AddRefreshIntervalMenuItem(refreshIntervalMenu, "2 分钟", 120);
        AddRefreshIntervalMenuItem(refreshIntervalMenu, "5 分钟", 300);
        menu.Items.Add(refreshIntervalMenu);

        menu.Items.Add("任务栏显示设置…", null, (_, _) => Dispatcher.Invoke(ShowTaskbarSettings));

        _taskbarMenuItem = new Forms.ToolStripMenuItem("显示任务栏文本条")
        {
            Checked = _settings?.ShowTaskbarBar ?? true,
            CheckOnClick = false,
        };
        _taskbarMenuItem.Click += (_, _) => Dispatcher.Invoke(
            () => SetTaskbarBarVisible(!(_settings?.ShowTaskbarBar ?? false)));
        UpdateTaskbarMenuState(_settings?.ShowTaskbarBar ?? false);
        menu.Items.Add(_taskbarMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        _currentIcon = TrayIconFactory.Create(null, null);
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _currentIcon,
            Text = "TokenMonitor 正在启动",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                Dispatcher.Invoke(ToggleWindow);
            }
        };
    }

    private void OnSnapshotsChanged(object? sender, EventArgs e)
    {
        if (_trayIcon is null || _viewModel is null)
        {
            return;
        }

        _trayIcon.Text = _viewModel.TrayTooltip;

        var nextIcon = TrayIconFactory.Create(
            _viewModel.Codex.RemainingPercent,
            _viewModel.Claude.RemainingPercent);
        var previousIcon = _currentIcon;
        _currentIcon = nextIcon;
        _trayIcon.Icon = nextIcon;
        previousIcon?.Dispose();
    }

    private void ToggleWindow()
    {
        if (_window is null)
        {
            return;
        }

        if (_window.IsVisible)
        {
            _window.Hide();
        }
        else
        {
            ShowWindow();
        }
    }

    private void ShowWindow() => _window?.ShowPopup();

    private void AddRefreshIntervalMenuItem(
        Forms.ToolStripMenuItem parent,
        string label,
        int seconds)
    {
        var item = new Forms.ToolStripMenuItem(label)
        {
            Checked = _settings?.RefreshIntervalSeconds == seconds,
            CheckOnClick = false,
        };
        item.Click += (_, _) => Dispatcher.Invoke(() => SetRefreshInterval(seconds));
        _refreshIntervalMenuItems[seconds] = item;
        parent.DropDownItems.Add(item);
    }

    private void SetRefreshInterval(int seconds)
    {
        if (_settings is null || _settingsStore is null)
        {
            return;
        }

        _settings.RefreshIntervalSeconds = seconds;
        _settingsStore.Save(_settings);
        _window?.SetRefreshInterval(TimeSpan.FromSeconds(seconds));

        foreach (var pair in _refreshIntervalMenuItems)
        {
            pair.Value.Checked = pair.Key == seconds;
        }
    }

    private void ShowTaskbarSettings()
    {
        if (_settings is null || _settingsStore is null)
        {
            return;
        }

        var dialog = new TaskbarSettingsWindow(_settings);
        if (_window?.IsVisible == true)
        {
            dialog.Owner = _window;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _settingsStore.Save(_settings);
        _taskbarWindow?.ApplyTaskbarTextSettings();
    }

    private void SetTaskbarBarVisible(bool visible)
    {
        if (_settings is null || _settingsStore is null)
        {
            return;
        }

        if (visible)
        {
            ReloadSettingsFromDisk(recreateTaskbar: false);
            if (_settings is null || _settingsStore is null)
            {
                return;
            }

            _settings.ShowTaskbarBar = true;
            _settingsStore.Save(_settings);
            CloseTaskbarWindow();
            CreateTaskbarWindow();
            _taskbarWindow?.Show();
            _taskbarWindow?.EnsurePlacement();
        }
        else
        {
            _settings.ShowTaskbarBar = false;
            _settingsStore.Save(_settings);
            CloseTaskbarWindow();
        }

        UpdateTaskbarMenuState(visible);
    }

    private void ActivateExistingInstance()
    {
        if (_isExiting)
        {
            return;
        }

        ReloadSettingsFromDisk();
        ShowWindow();
    }

    private void ReloadSettingsFromDisk(bool recreateTaskbar = true)
    {
        if (_settingsStore is null)
        {
            return;
        }

        _settings = _settingsStore.Load();
        _window?.SetRefreshInterval(TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds));
        foreach (var pair in _refreshIntervalMenuItems)
        {
            pair.Value.Checked = pair.Key == _settings.RefreshIntervalSeconds;
        }

        if (recreateTaskbar)
        {
            CloseTaskbarWindow();
            if (_settings.ShowTaskbarBar)
            {
                CreateTaskbarWindow();
                _taskbarWindow?.Show();
            }
        }

        UpdateTaskbarMenuState(_settings.ShowTaskbarBar);
    }

    private void UpdateTaskbarMenuState(bool visible)
    {
        if (_taskbarMenuItem is null)
        {
            return;
        }

        _taskbarMenuItem.Checked = visible;
        _taskbarMenuItem.Text = visible ? "隐藏任务栏文本条" : "显示任务栏文本条";
    }

    private void OnExitRequested(object? sender, EventArgs e) => ExitApplication();

    private async void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
        }

        _window?.AllowClose();
        _window?.Close();
        CloseTaskbarWindow();

        if (_viewModel is not null)
        {
            _viewModel.SnapshotsChanged -= OnSnapshotsChanged;
            await _viewModel.DisposeAsync();
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _currentIcon?.Dispose();

        _activationRegistration?.Unregister(null);
        _activationEvent?.Dispose();

        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();

        base.OnExit(e);
    }
}
