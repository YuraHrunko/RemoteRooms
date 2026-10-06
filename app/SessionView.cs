using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RemoteViewing.Vnc;
using RemoteViewing.WPF;
using Renci.SshNet;
using System.Security.Cryptography;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsFormsHost = System.Windows.Forms.Integration.WindowsFormsHost;

namespace RemoteRooms;

public sealed class SessionView : UserControl, IDisposable
{
    private readonly Endpoint endpoint;
    private readonly string folder;
    private readonly string? password;
    private readonly TextBlock status;
    private readonly TextBlock? vncSecurityInfo;
    private readonly Grid surface;
    private readonly ComboBox? vncMode;
    private readonly Button reconnect;
    private readonly Button windowModeButton;
    private readonly Button localDisplayButton;
    private Action? windowModeAction;
    private readonly DockPanel root;
    private readonly Border bar;
    private readonly StringBuilder terminalBuffer = new();
    private VncControl? vnc;
    private ScrollViewer? vncScroller;
    private Canvas? vncCanvas;
    private readonly List<Int32Rect> vncScreens = [];
    private int vncFrameWidth;
    private int vncFrameHeight;
    private bool updatingVncModes;
    private RdpAxHost? rdp;
    private SshClient? ssh;
    private ShellStream? shell;
    private WebView2? terminal;
    private bool terminalReady;
    private bool disposed;
    private bool disconnected;
    private bool lightTheme = true;
    private bool immersive;
    private string? currentError;
    private int attempt;
    private DispatcherTimer? rdpStateTimer;
    private bool rdpWasConnected;

    public SessionView(Endpoint endpoint, string folder, string? password)
    {
        this.endpoint = endpoint;
        this.folder = folder;
        this.password = password;
        root = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(241, 245, 249)) };
        bar = new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(223, 230, 237)),
            BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 6, 12, 6) };
        DockPanel.SetDock(bar, Dock.Top);
        var row = new DockPanel();
        status = new TextBlock { Text = $"Підключення до {endpoint.Host}:{endpoint.Port}…",
            Foreground = new SolidColorBrush(Color.FromRgb(27, 42, 57)), FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        windowModeButton = new Button { Content = "На весь екран", Padding = new Thickness(13, 6, 13, 6),
            Background = new SolidColorBrush(Color.FromRgb(11, 129, 120)), BorderThickness = new Thickness(0),
            Foreground = Brushes.White, Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Esc або F11 — вихід із повного екрана", Margin = new Thickness(8, 0, 0, 0) };
        windowModeButton.Click += (_, _) => windowModeAction?.Invoke();
        DockPanel.SetDock(windowModeButton, Dock.Right);
        row.Children.Add(windowModeButton);
        localDisplayButton = new Button { Content = "Монітор", Padding = new Thickness(10, 6, 10, 6),
            Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(223, 230, 237)),
            Foreground = new SolidColorBrush(Color.FromRgb(27, 42, 57)), Cursor = System.Windows.Input.Cursors.Hand,
            Visibility = Visibility.Collapsed, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Вибрати локальний монітор" };
        localDisplayButton.Click += (_, _) => { if (localDisplayButton.ContextMenu is { } menu) menu.IsOpen = true; };
        DockPanel.SetDock(localDisplayButton, Dock.Right);
        row.Children.Add(localDisplayButton);
        reconnect = new Button { Content = "Від’єднати", Padding = new Thickness(12, 5, 12, 5),
            Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(223, 230, 237)),
            Foreground = new SolidColorBrush(Color.FromRgb(27, 42, 57)), Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(8, 0, 0, 0) };
        reconnect.Click += async (_, _) => { if (disconnected) await RetryAsync(); else Disconnect(); };
        DockPanel.SetDock(reconnect, Dock.Right);
        row.Children.Add(reconnect);
        if (endpoint.Protocol == "VNC")
        {
            vncMode = new ComboBox { ItemsSource = new[] { "Усі екрани", "100% · прокручування" },
                SelectedIndex = 0, Width = 198, Margin = new Thickness(8, 0, 8, 0) };
            vncMode.SelectionChanged += (_, _) =>
            {
                if (!updatingVncModes) ApplyVncDisplayMode();
            };
            DockPanel.SetDock(vncMode, Dock.Right);
            row.Children.Add(vncMode);
        }
        var statusLine = new Grid();
        statusLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusLine.Children.Add(status);
        if (endpoint.Protocol == "VNC")
        {
            statusLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            vncSecurityInfo = new TextBlock { Text = "ⓘ", FontSize = 15,
                Foreground = new SolidColorBrush(Color.FromRgb(96, 113, 125)),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "Шифрування VNC-каналу не підтверджено. Використовуйте довірену мережу або VPN." };
            System.Windows.Automation.AutomationProperties.SetName(vncSecurityInfo, "Шифрування VNC не підтверджено");
            Grid.SetColumn(vncSecurityInfo, 1); statusLine.Children.Add(vncSecurityInfo);
        }
        row.Children.Add(statusLine);
        bar.Child = row;
        root.Children.Add(bar);
        surface = new Grid();
        surface.SizeChanged += (_, _) => ApplyVncDisplayMode();
        root.Children.Add(surface);
        Content = root;
        ApplySurfaceTheme();
        Loaded += async (_, _) => { if (surface.Children.Count == 0 && !disconnected && !disposed) await ConnectAsync(); };
    }

    public void ApplyTheme(bool light)
    {
        lightTheme = light;
        root.Background = new SolidColorBrush(light ? Color.FromRgb(241, 245, 249) : Color.FromRgb(15, 23, 35));
        bar.Background = new SolidColorBrush(light ? Colors.White : Color.FromRgb(23, 34, 49));
        bar.BorderBrush = new SolidColorBrush(light ? Color.FromRgb(223, 230, 237) : Color.FromRgb(51, 68, 86));
        status.Foreground = new SolidColorBrush(light ? Color.FromRgb(27, 42, 57) : Color.FromRgb(236, 244, 249));
        if (vncSecurityInfo != null)
            vncSecurityInfo.Foreground = new SolidColorBrush(light ? Color.FromRgb(96, 113, 125) : Color.FromRgb(163, 183, 194));
        reconnect.Background = new SolidColorBrush(light ? Colors.White : Color.FromRgb(34, 47, 64));
        reconnect.BorderBrush = bar.BorderBrush;
        reconnect.Foreground = status.Foreground;
        localDisplayButton.Background = reconnect.Background;
        localDisplayButton.BorderBrush = bar.BorderBrush;
        localDisplayButton.Foreground = status.Foreground;
        windowModeButton.Background = new SolidColorBrush(light ? Color.FromRgb(11, 129, 120) : Color.FromRgb(71, 204, 184));
        windowModeButton.Foreground = light ? Brushes.White : new SolidColorBrush(Color.FromRgb(15, 24, 31));
        ApplySurfaceTheme();
        if (currentError != null) ShowError(currentError);
    }

    public void SetImmersive(bool value)
    {
        immersive = value;
        ApplySurfaceTheme();
        if (currentError != null) ShowError(currentError);
    }

    private void ApplySurfaceTheme() => surface.Background = new SolidColorBrush(
        immersive ? Color.FromRgb(15, 24, 31) : lightTheme ? Color.FromRgb(241, 245, 249) : Color.FromRgb(15, 23, 35));

    public Endpoint Endpoint => endpoint;

    public void ConfigureWindowActions(string label, Action action, ContextMenu? localDisplays = null)
    {
        windowModeAction = action;
        windowModeButton.Content = label;
        windowModeButton.ToolTip = label == "На весь екран" ? "Відкрити сеанс на весь екран · F11" :
            "Повернутися до вкладки · Esc або F11";
        localDisplayButton.ContextMenu = localDisplays;
        localDisplayButton.Visibility = localDisplays == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetStatus(string value) => Dispatcher.Invoke(() => status.Text = value);

    private async Task ConnectAsync()
    {
        if (disposed || disconnected) return;
        var currentAttempt = ++attempt;
        currentError = null;
        reconnect.Content = "Від’єднати";
        SetStatus($"{endpoint.Protocol} · підключення до {endpoint.Host}:{endpoint.Port}…");
        ShowConnecting();
        try
        {
            switch (endpoint.Protocol)
            {
                case "RDP":
                    surface.Children.Clear();
                    rdp = new RdpAxHost();
                    var host = new WindowsFormsHost { Child = rdp };
                    surface.Children.Add(host);
                    rdp.CreateControl();
                    var dpi = VisualTreeHelper.GetDpi(surface);
                    rdp.Start(endpoint, password,
                        Math.Max(1024, (int)Math.Round(surface.ActualWidth * dpi.DpiScaleX)),
                        Math.Max(768, (int)Math.Round(surface.ActualHeight * dpi.DpiScaleY)));
                    SetStatus("RDP · очікування відповіді сервера…");
                    WatchRdpState(currentAttempt);
                    break;
                case "VNC":
                    surface.Children.Clear();
                    var vncControl = new VncControl
                    {
                        SizeMode = VncControlSizeMode.Clip,
                        Width = 1,
                        Height = 1
                    };
                    vnc = vncControl;
                    vncCanvas = new Canvas { ClipToBounds = true, Background = Brushes.Black };
                    vncCanvas.Children.Add(vncControl);
                    vncScroller = new ScrollViewer
                    {
                        Content = vncCanvas,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                    };
                    surface.Children.Add(vncScroller);
                    ApplyVncDisplayMode();
                    vncControl.FramebufferChanged += (_, _) =>
                    {
                        if (disposed || disconnected || currentAttempt != attempt) return;
                        var frame = vncControl.Client?.Framebuffer;
                        if (frame == null) return;
                        if (frame.Width != vncFrameWidth || frame.Height != vncFrameHeight)
                        {
                            UpdateVncScreens(frame.Width, frame.Height);
                            ApplyVncDisplayMode();
                        }
                        status.Text = $"VNC · зображення {frame.Width}×{frame.Height}";
                    };
                    vncControl.Closed += (_, _) => Dispatcher.BeginInvoke(() =>
                    {
                        if (disposed || disconnected || currentAttempt != attempt) return;
                        disconnected = true;
                        reconnect.Content = "Повторити";
                        status.Text = "VNC · з’єднання закрито";
                        ShowError("Сервер закрив VNC-з’єднання.");
                    });
                    var options = new VncClientConnectOptions();
                    if (password != null) options.Password = password.ToCharArray();
                    await Task.Run(() => vncControl.Client.Connect(endpoint.Host, endpoint.Port, options));
                    if (disposed || disconnected || currentAttempt != attempt) { vncControl.Client.Close(); return; }
                    if (vncControl.Client.Framebuffer == null)
                        SetStatus("VNC · очікування зображення…");
                    break;
                case "SSH":
                    surface.Children.Clear();
                    await StartSshAsync(currentAttempt);
                    break;
            }
        }
        catch (Exception ex)
        {
            if (disposed || currentAttempt != attempt) return;
            DisconnectResources();
            disconnected = true;
            reconnect.Content = "Повторити";
            SetStatus($"{endpoint.Protocol} · не вдалося підключитися");
            ShowError(FriendlyError(ex));
        }
    }

    private void ApplyVncDisplayMode()
    {
        if (vnc is not { } control || vncScroller == null || vncCanvas is not { } canvas) return;
        var viewportWidth = Math.Max(1, surface.ActualWidth);
        var viewportHeight = Math.Max(1, surface.ActualHeight);
        var frameWidth = Math.Max(1, vncFrameWidth);
        var frameHeight = Math.Max(1, vncFrameHeight);
        control.Width = frameWidth;
        control.Height = frameHeight;
        control.SizeMode = VncControlSizeMode.Clip;
        var selected = vncMode?.SelectedIndex ?? 0;
        var nativeSize = selected == vncScreens.Count + 1;
        if (nativeSize)
        {
            canvas.Width = frameWidth;
            canvas.Height = frameHeight;
            control.Clip = null;
            control.RenderTransform = Transform.Identity;
            Canvas.SetLeft(control, 0);
            Canvas.SetTop(control, 0);
        }
        else
        {
            canvas.Width = viewportWidth;
            canvas.Height = viewportHeight;
            var region = selected > 0 && selected <= vncScreens.Count
                ? vncScreens[selected - 1]
                : new Int32Rect(0, 0, frameWidth, frameHeight);
            var scale = Math.Min(viewportWidth / region.Width, viewportHeight / region.Height);
            control.Clip = selected > 0 ? new RectangleGeometry(new Rect(region.X, region.Y, region.Width, region.Height)) : null;
            control.RenderTransform = new ScaleTransform(scale, scale);
            Canvas.SetLeft(control, (viewportWidth - region.Width * scale) / 2 - region.X * scale);
            Canvas.SetTop(control, (viewportHeight - region.Height * scale) / 2 - region.Y * scale);
        }
        vncScroller.HorizontalScrollBarVisibility = nativeSize ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        vncScroller.VerticalScrollBarVisibility = nativeSize ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }

    private void UpdateVncScreens(int width, int height)
    {
        var selected = vncMode?.SelectedIndex ?? 0;
        var wasNativeSize = selected == vncScreens.Count + 1;
        vncFrameWidth = width;
        vncFrameHeight = height;
        vncScreens.Clear();
        vncScreens.AddRange(VncScreenLayout.Infer(width, height));
        if (vncMode == null) return;
        var names = new List<string> { "Усі екрани" };
        for (var i = 0; i < vncScreens.Count; i++)
        {
            var screen = vncScreens[i];
            names.Add($"Екран {i + 1} · {screen.Width}×{screen.Height}");
        }
        names.Add("100% · прокручування");
        updatingVncModes = true;
        try
        {
            vncMode.ItemsSource = names;
            vncMode.SelectedIndex = wasNativeSize ? vncScreens.Count + 1 : Math.Min(selected, vncScreens.Count);
        }
        finally { updatingVncModes = false; }
    }

    private static string FriendlyError(Exception ex)
    {
        var socket = ex as SocketException ?? ex.InnerException as SocketException;
        return socket?.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => "Сервер відхилив з’єднання. Перевірте адресу, порт і доступність служби.",
            SocketError.HostNotFound => "Не вдалося знайти сервер. Перевірте адресу.",
            SocketError.TimedOut => "Час очікування вичерпано. Перевірте мережу та доступність сервера.",
            _ => ex.Message
        };
    }

    private void WatchRdpState(int currentAttempt)
    {
        rdpStateTimer?.Stop();
        rdpWasConnected = false;
        var started = DateTime.UtcNow;
        rdpStateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        rdpStateTimer.Tick += (_, _) =>
        {
            if (disposed || disconnected || currentAttempt != attempt) { rdpStateTimer?.Stop(); return; }
            var state = rdp?.ConnectionState ?? -1;
            if (state == 1)
            {
                if (!rdpWasConnected) { rdpWasConnected = true; status.Text = "RDP · підключено"; }
                return;
            }
            if (state == 0 && (rdpWasConnected || DateTime.UtcNow - started > TimeSpan.FromSeconds(5)))
            {
                rdpStateTimer?.Stop();
                disconnected = true; reconnect.Content = "Повторити";
                DisconnectResources();
                status.Text = "RDP · з’єднання закрито";
                ShowError(rdpWasConnected ? "Сервер закрив RDP-з’єднання." : "Не вдалося підключитися до RDP-сервера. Перевірте адресу та порт.");
                return;
            }
            if (!rdpWasConnected && DateTime.UtcNow - started > TimeSpan.FromSeconds(30))
                status.Text = "RDP · немає підтвердження від сервера. Перевірте адресу або повторіть.";
        };
        rdpStateTimer.Start();
    }

    private void ShowError(string message)
    {
        currentError = message;
        surface.Children.Clear();
        var dark = immersive || !lightTheme;
        var card = new StackPanel { Width = 430, HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center };
        card.Children.Add(new TextBlock { Text = "○", FontSize = 50, TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(18, 137, 123)) });
        card.Children.Add(new TextBlock { Text = "Не вдалося відкрити сеанс", FontSize = 21,
            FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(dark ? Color.FromRgb(237, 244, 246) : Color.FromRgb(27, 42, 57)),
            Margin = new Thickness(0, 9, 0, 8) });
        card.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(dark ? Color.FromRgb(163, 183, 194) : Color.FromRgb(108, 124, 139)) });
        var retry = new Button { Content = "Повторити підключення", Margin = new Thickness(110, 22, 110, 0),
            Padding = new Thickness(12, 8, 12, 8), Background = new SolidColorBrush(Color.FromRgb(18, 137, 123)),
            Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
        retry.Click += async (_, _) => await RetryAsync();
        card.Children.Add(retry);
        surface.Children.Add(card);
    }

    private void ShowConnecting()
    {
        surface.Children.Clear();
        surface.Children.Add(new TextBlock
        {
            Text = $"Підключення до {endpoint.Host}…", FontSize = 15,
            Foreground = new SolidColorBrush(immersive || !lightTheme ? Color.FromRgb(163, 183, 194) : Color.FromRgb(96, 113, 125)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
    }

    private async Task RetryAsync()
    {
        if (disposed) return;
        DisconnectResources();
        surface.Children.Clear();
        disconnected = false;
        terminalReady = false;
        terminalBuffer.Clear();
        await ConnectAsync();
    }

    private async Task StartSshAsync(int currentAttempt)
    {
        var webTerminal = new WebView2();
        terminal = webTerminal;
        surface.Children.Add(webTerminal);
        await webTerminal.EnsureCoreWebView2Async();
        if (disposed || disconnected || currentAttempt != attempt) return;
        webTerminal.CoreWebView2.SetVirtualHostNameToFolderMapping("remoterooms.local", Path.Combine(AppContext.BaseDirectory, "assets"), CoreWebView2HostResourceAccessKind.DenyCors);
        webTerminal.CoreWebView2.WebMessageReceived += (_, e) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var type = doc.RootElement.GetProperty("type").GetString();
                if (type == "ready")
                {
                    terminalReady = true;
                    if (terminalBuffer.Length > 0)
                    {
                        var pending = terminalBuffer.ToString(); terminalBuffer.Clear();
                        _ = webTerminal.ExecuteScriptAsync("sendOutput(" + JsonSerializer.Serialize(pending) + ");");
                    }
                }
                if (type == "input" && shell != null) shell.Write(doc.RootElement.GetProperty("data").GetString() ?? "");
                if (type == "resize" && shell != null)
                {
                    var cols = doc.RootElement.GetProperty("cols").GetInt32();
                    var rows = doc.RootElement.GetProperty("rows").GetInt32();
                    shell.ChangeWindowSize((uint)Math.Max(1, cols), (uint)Math.Max(1, rows), 0, 0);
                }
            }
            catch { /* Ignore malformed local terminal messages. */ }
        };
        webTerminal.Source = new Uri("https://remoterooms.local/terminal.html");
        if (string.IsNullOrWhiteSpace(endpoint.Username)) throw new InvalidOperationException("Укажіть користувача SSH у властивостях підключення.");
        if (password == null) throw new InvalidOperationException("Збережіть пароль SSH у властивостях підключення.");
        var sshClient = new SshClient(endpoint.Host, endpoint.Port, endpoint.Username, password);
        ssh = sshClient;
        sshClient.HostKeyReceived += (_, e) =>
        {
            var key = Convert.ToBase64String(SHA256.HashData(e.HostKey));
            var knownPath = Path.Combine(folder, "known-hosts.json");
            Dictionary<string, string> known;
            try { known = File.Exists(knownPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(knownPath)) ?? [] : []; }
            catch { known = []; }
            var identity = endpoint.Host + ":" + endpoint.Port;
            if (known.TryGetValue(identity, out var old) && old == key) { e.CanTrust = true; return; }
            var changed = old != null;
            var answer = Dispatcher.Invoke(() => MessageBox.Show(
                $"{(changed ? "Ключ SSH-сервера змінився!" : "Новий ключ SSH-сервера") }\n{identity}\nSHA-256: {key}\n\nДовіряти цьому ключу?",
                "Перевірка сервера SSH", MessageBoxButton.YesNo, changed ? MessageBoxImage.Warning : MessageBoxImage.Question));
            e.CanTrust = answer == MessageBoxResult.Yes;
            if (e.CanTrust) { known[identity] = key; File.WriteAllText(knownPath, JsonSerializer.Serialize(known)); }
        };
        await Task.Run(() => sshClient.Connect());
        if (disposed || disconnected || currentAttempt != attempt) { sshClient.Disconnect(); return; }
        var shellStream = sshClient.CreateShellStream("xterm", 100, 30, 1000, 700, 4096);
        shell = shellStream;
        SetStatus("SSH · підключено");
        _ = Task.Run(async () =>
        {
            var bytes = new byte[8192];
            var decoder = Encoding.UTF8.GetDecoder();
            var chars = new char[8192];
            try
            {
                while (!disposed && !disconnected && currentAttempt == attempt && sshClient.IsConnected)
                {
                    var count = shellStream.Read(bytes, 0, bytes.Length);
                    if (count <= 0) break;
                    var length = decoder.GetChars(bytes, 0, count, chars, 0);
                    var chunk = new string(chars, 0, length);
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        if (terminal?.CoreWebView2 == null || disposed || currentAttempt != attempt) return;
                        if (terminalReady) await terminal.ExecuteScriptAsync("sendOutput(" + JsonSerializer.Serialize(chunk) + ");");
                        else terminalBuffer.Append(chunk);
                    }).Task.Unwrap();
                }
            }
            catch (Exception ex) { if (!disposed && currentAttempt == attempt) SetStatus("SSH · " + ex.Message); }
            finally
            {
                if (!disposed && currentAttempt == attempt)
                    Dispatcher.Invoke(() => { disconnected = true; reconnect.Content = "Повторити";
                        status.Text = "SSH · з’єднання закрито"; ShowError("Сервер закрив з’єднання."); });
            }
        });
    }

    public void Disconnect()
    {
        if (disposed) return;
        ++attempt;
        disconnected = true;
        DisconnectResources();
        surface.Children.Clear();
        currentError = null;
        reconnect.Content = "Повторити";
        SetStatus("Від’єднано");
    }
    private void DisconnectResources()
    {
        rdpStateTimer?.Stop(); rdpStateTimer = null;
        try { shell?.Dispose(); } catch { }
        try { ssh?.Disconnect(); ssh?.Dispose(); } catch { }
        try { vnc?.Client.Close(); } catch { }
        try { rdp?.Stop(); } catch { }
        try { terminal?.Dispose(); } catch { }
        try { rdp?.Dispose(); } catch { }
        shell = null; ssh = null; vnc = null; vncScroller = null; vncCanvas = null;
        vncScreens.Clear(); vncFrameWidth = 0; vncFrameHeight = 0;
        rdp = null; terminal = null;
    }
    public void Dispose() { Disconnect(); disposed = true; }
}

internal sealed class RdpAxHost : System.Windows.Forms.AxHost
{
    private dynamic? ocx;
    public RdpAxHost() : base("1DF7C823-B2D4-4B54-975A-F2AC5D7CF8B8") { }
    public void Start(Endpoint endpoint, string? password, int desktopWidth, int desktopHeight)
    {
        ocx = GetOcx();
        if (ocx == null) throw new InvalidOperationException("RDP-компонент Windows недоступний.");
        ocx.Server = endpoint.Host;
        ocx.UserName = endpoint.Username;
        ocx.AdvancedSettings2.RDPPort = endpoint.Port;
        if (password != null) ocx.AdvancedSettings2.ClearTextPassword = password;
        ocx.ColorDepth = 32;
        ocx.DesktopWidth = desktopWidth;
        ocx.DesktopHeight = desktopHeight;
        ocx.Connect();
    }
    public void Stop() { try { ocx?.Disconnect(); } catch { } }
    public int ConnectionState
    {
        get { try { return ocx == null ? -1 : Convert.ToInt32(ocx.Connected); } catch { return -1; } }
    }
}
