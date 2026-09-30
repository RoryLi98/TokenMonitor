using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TokenMonitor.App.Infrastructure;
using TokenMonitor.App.ViewModels;

namespace TokenMonitor.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _refreshTimer;
    private bool _initialized;
    private bool _allowClose;
    private HwndSource? _windowSource;
    private static readonly int TaskbarCreatedMessage = (int)RegisterWindowMessage("TaskbarCreated");

    internal MainWindow(MainViewModel viewModel, AppSettings settings)
    {
        InitializeComponent();
#if DEBUG
        ShowInTaskbar = true;
#endif
        _viewModel = viewModel;
        DataContext = viewModel;
        ApplyPopupSettings(settings);

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => _viewModel.UpdateClock();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _refreshTimer.Tick += async (_, _) => await _viewModel.RefreshAsync();

        Loaded += OnLoaded;
        SizeChanged += (_, _) =>
        {
            if (IsVisible)
            {
                PositionNearTaskbar();
            }
        };
        Closing += OnClosing;
        SourceInitialized += OnSourceInitialized;
    }

    public event EventHandler? TaskbarRecreated;

    public void ShowPopup()
    {
        Show();
        UpdateLayout();
        PositionNearTaskbar();
        Activate();
    }

    public void AllowClose() => _allowClose = true;

    public void SetRefreshInterval(TimeSpan interval)
    {
        _refreshTimer.Interval = interval;
    }

    internal void ApplyPopupSettings(AppSettings settings)
    {
        Width = settings.PopupWidth;
        PopupBorder.Padding = new Thickness(
            settings.PopupPadding,
            settings.PopupPadding,
            settings.PopupPadding,
            settings.PopupBottomPadding);
        PopupBorder.CornerRadius = new CornerRadius(settings.PopupCornerRadius);
        var headerScale = settings.PopupHeaderScalePercent / 100.0;
        HeaderLogo.Width = 22 * headerScale;
        HeaderLogo.Height = 22 * headerScale;
        HeaderLogo.Margin = new Thickness(0, 0, 7 * headerScale, 0);
        HeaderTitle.FontSize = 17 * headerScale;
        foreach (var button in new[] { RefreshButton, HideButton })
        {
            button.Width = 28 * headerScale;
            button.Height = 28 * headerScale;
            button.Margin = new Thickness(4 * headerScale, 0, 0, 0);
            button.FontSize = 15 * headerScale;
        }
        PopupHeader.Margin = new Thickness(1, 0, 1, 6 * headerScale);
        if (IsVisible)
        {
            UpdateLayout();
            PositionNearTaskbar();
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionNearTaskbar();
        _clockTimer.Start();
        _refreshTimer.Start();

        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await _viewModel.RefreshAsync();
    }

    private void PositionNearTaskbar()
    {
        var workArea = SystemParameters.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : 160;
        Left = Math.Max(workArea.Left + 12, workArea.Right - width - 14);
        Top = Math.Max(workArea.Top + 12, workArea.Bottom - height - 14);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            _clockTimer.Stop();
            _refreshTimer.Stop();
            _windowSource?.RemoveHook(WindowMessageHook);
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == TaskbarCreatedMessage)
        {
            TaskbarRecreated?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string messageName);

}
