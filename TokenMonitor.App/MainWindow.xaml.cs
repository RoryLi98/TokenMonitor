using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
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

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
#if DEBUG
        ShowInTaskbar = true;
#endif
        _viewModel = viewModel;
        DataContext = viewModel;

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => _viewModel.UpdateClock();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _refreshTimer.Tick += async (_, _) => await _viewModel.RefreshAsync();

        Loaded += OnLoaded;
        Closing += OnClosing;
        SourceInitialized += OnSourceInitialized;
    }

    public event EventHandler? TaskbarRecreated;

    public void ShowPopup()
    {
        PositionNearTaskbar();
        Show();
        Activate();
    }

    public void AllowClose() => _allowClose = true;

    public void SetRefreshInterval(TimeSpan interval)
    {
        _refreshTimer.Interval = interval;
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
        Left = Math.Max(workArea.Left + 12, workArea.Right - Width - 14);
        Top = Math.Max(workArea.Top + 12, workArea.Bottom - Height - 14);
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
