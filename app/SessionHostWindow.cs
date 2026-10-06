using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Screen = System.Windows.Forms.Screen;

namespace RemoteRooms;

internal sealed class SessionHostWindow : Window
{
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private readonly Action<SessionView> returnToTab;
    private readonly bool returnToTabOnExit;
    private Rect windowedBounds;
    private bool fullscreen;
    private bool releasedToTab;
    private bool closingForShutdown;

    public SessionView View { get; }
    public bool IsFullscreen => fullscreen;

    public SessionHostWindow(Window owner, SessionView view, bool startFullscreen, Action<SessionView> returnToTab)
    {
        Owner = owner;
        View = view;
        this.returnToTab = returnToTab;
        returnToTabOnExit = startFullscreen;
        fullscreen = startFullscreen;
        Title = $"{view.Endpoint.Protocol} · {view.Endpoint.Host} — RemoteRooms";
        Background = System.Windows.Media.Brushes.Black;
        Content = view;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = owner.Left;
        Top = owner.Top;
        Width = startFullscreen ? Math.Max(800, owner.Width) : 1180;
        Height = startFullscreen ? Math.Max(600, owner.Height) : 800;
        MinWidth = startFullscreen ? 0 : 680;
        MinHeight = startFullscreen ? 0 : 440;
        WindowStyle = startFullscreen ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        ResizeMode = startFullscreen ? ResizeMode.NoResize : ResizeMode.CanResize;
        ShowInTaskbar = true;
        ConfigureActions();
        SourceInitialized += (_, _) =>
        {
            if (startFullscreen)
            {
                PlaceOnScreen(Screen.FromHandle(new WindowInteropHelper(owner).Handle));
                ConfigureActions();
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (fullscreen && (e.Key == Key.Escape || e.Key == Key.F11))
            {
                ExitFullscreen(); e.Handled = true;
            }
            else if (!fullscreen && e.Key == Key.F11)
            {
                EnterFullscreen(); e.Handled = true;
            }
        };
        Closing += OnClosing;
        Closed += (_, _) => { if (!releasedToTab) view.Dispose(); };
    }

    private void ConfigureActions()
    {
        View.SetImmersive(fullscreen);
        View.ConfigureWindowActions(fullscreen ? "Вийти з повного екрана" : "На весь екран",
            fullscreen ? ExitFullscreen : EnterFullscreen, fullscreen ? BuildMonitorMenu() : null);
    }

    private ContextMenu? BuildMonitorMenu()
    {
        var screens = Screen.AllScreens;
        if (screens.Length < 2) return null;
        var current = Screen.FromHandle(new WindowInteropHelper(this).Handle);
        var menu = new ContextMenu();
        for (var i = 0; i < screens.Length; i++)
        {
            var target = screens[i];
            var item = new MenuItem { Header = $"Монітор {i + 1}{(target.Primary ? " · основний" : "")}",
                IsCheckable = true, IsChecked = target.DeviceName == current.DeviceName };
            item.Click += (_, _) => { PlaceOnScreen(target); ConfigureActions(); };
            menu.Items.Add(item);
        }
        return menu;
    }

    private void PlaceOnScreen(Screen target)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        WindowState = WindowState.Normal;
        SetWindowPos(handle, IntPtr.Zero, target.Bounds.Left, target.Bounds.Top,
            Math.Max(600, target.Bounds.Width / 2), Math.Max(400, target.Bounds.Height / 2),
            SwpNoZOrder | SwpNoActivate);
        WindowState = WindowState.Maximized;
    }

    private void EnterFullscreen()
    {
        if (fullscreen) return;
        windowedBounds = new Rect(Left, Top, Width, Height);
        var current = Screen.FromHandle(new WindowInteropHelper(this).Handle);
        fullscreen = true;
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        PlaceOnScreen(current);
        ConfigureActions();
    }

    private void ExitFullscreen()
    {
        if (!fullscreen || closingForShutdown) return;
        if (returnToTabOnExit)
        {
            releasedToTab = true;
            closingForShutdown = true;
            Content = null;
            Close();
            returnToTab(View);
            return;
        }
        fullscreen = false;
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        Left = windowedBounds.Left;
        Top = windowedBounds.Top;
        Width = windowedBounds.Width;
        Height = windowedBounds.Height;
        ConfigureActions();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!fullscreen || closingForShutdown) return;
        e.Cancel = true;
        Dispatcher.BeginInvoke(ExitFullscreen);
    }

    public void PrepareForOwnerShutdown() => closingForShutdown = true;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);
}
