using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Shapes;

namespace RemoteRooms;

public sealed class MainWindow : Window
{
    private const double DeskWidth = 154;
    private const double DeskHeight = 70;
    private const double PcWidth = 164;
    private const double PcHeight = 78;
    private readonly string folder;
    private WorkspaceData state = new();
    private string? roomId, computerId, deskId;
    private bool light = true;
    private SolidColorBrush bg = new(), panel = new(), surface = new(), text = new(), muted = new(), accent = new(), line = new();
    private StackPanel roomsList = null!, addressList = null!, details = null!;
    private TextBlock heading = null!, subtitle = null!, inspectorTitle = null!;
    private Canvas plan = null!;
    private TabControl tabs = null!;
    private TabItem planTab = null!;
    private TextBox search = null!;
    private double zoom = .8;
    private bool fitToWindow = true;
    private ScrollViewer roomView = null!;
    private ScrollViewer inspectorScroll = null!;
    private Button navButton = null!;
    private Button maximizeButton = null!;
    private HwndSource? windowSource;
    private bool sidebarOpen;
    private ColumnDefinition sidebarColumn = null!;
    private Border sidebarPanel = null!;
    private ColumnDefinition inspectorColumn = null!;
    private Border inspectorPanel = null!;
    private bool inspectorOpen;
    private readonly Dictionary<TabItem, SessionView> sessions = [];
    private readonly HashSet<SessionHostWindow> hostedSessions = [];

    public MainWindow(string folder)
    {
        this.folder = folder;
        Title = "RemoteRooms — віддалені робочі місця";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 55,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            UseAeroCaptionButtons = false
        });
        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(320, workArea.Width - 32);
        var availableHeight = Math.Max(320, workArea.Height - 32);
        Width = Math.Min(1040, availableWidth);
        Height = Math.Min(720, availableHeight);
        MinWidth = Math.Min(760, availableWidth);
        MinHeight = Math.Min(520, availableHeight);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        SetTheme();
        BuildShell();
        Refresh();
        SourceInitialized += (_, _) =>
        {
            windowSource = PresentationSource.FromVisual(this) as HwndSource;
            windowSource?.AddHook(ConstrainMaximizedWindow);
        };
        StateChanged += (_, _) => UpdateMaximizeButton();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && inspectorOpen)
            {
                inspectorOpen = false; UpdateInspector(); e.Handled = true; return;
            }
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox) return;
            if (e.Key == Key.F11 && Keyboard.Modifiers == ModifierKeys.None
                && tabs.SelectedItem is TabItem active && sessions.ContainsKey(active))
            {
                FullscreenTab(active); e.Handled = true; return;
            }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { if (CoreBridge.Undo()) Refresh(); e.Handled = true; }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) { if (CoreBridge.Redo()) Refresh(); e.Handled = true; }
            if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None && tabs.SelectedItem == planTab
                && Keyboard.FocusedElement is Border { Tag: string id })
            {
                if (id == deskId) { DeleteDesk(id); e.Handled = true; }
                else if (id == computerId) { DeleteComputer(id); e.Handled = true; }
            }
        };
        Closing += (_, _) => { foreach (var host in hostedSessions) host.PrepareForOwnerShutdown(); };
        Closed += (_, _) =>
        {
            windowSource?.RemoveHook(ConstrainMaximizedWindow);
            foreach (var host in hostedSessions.ToArray()) host.Close();
            foreach (var session in sessions.Values) session.Dispose();
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    private static IntPtr ConstrainMaximizedWindow(IntPtr window, int message, IntPtr wParam,
        IntPtr lParam, ref bool handled)
    {
        if (message != 0x0024) return IntPtr.Zero;
        var monitor = MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        limits.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        limits.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        limits.MaxSize.X = info.Work.Right - info.Work.Left;
        limits.MaxSize.Y = info.Work.Bottom - info.Work.Top;
        Marshal.StructureToPtr(limits, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    private void SetTheme()
    {
        bg = new(light ? Color.FromRgb(244, 247, 248) : Color.FromRgb(15, 24, 31));
        panel = new(light ? Colors.White : Color.FromRgb(25, 37, 46));
        surface = new(light ? Color.FromRgb(250, 252, 252) : Color.FromRgb(31, 45, 54));
        text = new(light ? Color.FromRgb(27, 43, 53) : Color.FromRgb(237, 244, 246));
        muted = new(light ? Color.FromRgb(96, 113, 125) : Color.FromRgb(163, 183, 194));
        accent = new(light ? Color.FromRgb(11, 129, 120) : Color.FromRgb(71, 204, 184));
        line = new(light ? Color.FromRgb(221, 230, 233) : Color.FromRgb(51, 70, 79));
    }

    private static Thickness Pad(double h, double v) => new(h, v, h, v);
    private TextBlock Label(string value, int size = 13, bool bold = false, Brush? brush = null) =>
        new() { Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = brush ?? text, VerticalAlignment = VerticalAlignment.Center };
    private static FrameworkElement Symbol(string kind, Brush color, double size)
    {
        var data = kind switch
        {
            "room" => "M2,2 L18,2 18,18 2,18 Z M8,2 L8,18 M8,9 L18,9",
            "desk" => "M2,7 L18,7 18,10 2,10 Z M4,10 L4,18 M16,10 L16,18",
            _ => "M2,3 L18,3 18,14 2,14 Z M10,14 L10,18 M6,18 L14,18"
        };
        return new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(data), Width = size, Height = size,
            Stroke = color, StrokeThickness = 1.7, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Stretch = Stretch.Uniform, HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }
    private Brush PlanBackground()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(panel.Color), null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        drawing.Children.Add(new GeometryDrawing(
            new SolidColorBrush(light ? Color.FromRgb(206, 219, 223) : Color.FromRgb(66, 86, 95)),
            null, new EllipseGeometry(new Point(12, 12), .8, .8)));
        return new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 24, 24),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 24, 24),
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };
    }
    private Button Btn(string value, RoutedEventHandler action, bool primary = false)
    {
        var b = new Button { Content = value, Padding = Pad(12, 7), Margin = new Thickness(0, 0, 7, 0),
            Background = primary ? accent : panel, Foreground = primary ? Brushes.White : text,
            BorderBrush = primary ? accent : line, BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
            FontSize = 12.5, FontWeight = FontWeights.SemiBold, MinHeight = 34 };
        var style = new Style(typeof(Button));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "ButtonBorder";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Button.PaddingProperty));
        border.AppendChild(presenter); template.VisualTree = border;
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.OpacityProperty, .67, border.Name));
        template.Triggers.Add(pressed);
        var focused = new Trigger { Property = Button.IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BorderBrushProperty, accent, border.Name));
        focused.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), border.Name));
        template.Triggers.Add(focused);
        style.Setters.Add(new Setter(Button.TemplateProperty, template));
        b.Style = style;
        b.Click += action;
        b.MouseEnter += (_, _) => Motion.Opacity(b, .84);
        b.MouseLeave += (_, _) => Motion.Opacity(b, 1);
        return b;
    }
    private Button DangerButton(string value, RoutedEventHandler action)
    {
        var button = Btn(value, action);
        button.Foreground = new SolidColorBrush(light ? Color.FromRgb(158, 62, 61) : Color.FromRgb(243, 152, 149));
        button.Margin = new Thickness(0);
        button.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        return button;
    }
    private static System.Windows.Shapes.Path CaptionIcon(string kind, Brush color) => new()
    {
        Data = Geometry.Parse(kind switch
        {
            "minimize" => "M2,10 L12,10",
            "restore" => "M4,2 L12,2 12,10 M2,4 L10,4 10,12 2,12 Z",
            "close" => "M2,2 L12,12 M12,2 L2,12",
            _ => "M2,2 L12,2 12,12 2,12 Z"
        }),
        Width = 14, Height = 14, Stretch = Stretch.Uniform,
        Stroke = color, StrokeThickness = 1.5,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round
    };
    private Button CaptionButton(string kind, string hint, RoutedEventHandler action, bool close = false)
    {
        var button = Btn("", action);
        button.Content = CaptionIcon(kind, text);
        button.Width = 39; button.Height = 34; button.MinHeight = 34;
        button.Margin = new Thickness(0); button.Padding = new Thickness(0);
        button.Background = Brushes.Transparent; button.BorderBrush = Brushes.Transparent;
        button.BorderThickness = new Thickness(0); button.ToolTip = hint;
        button.MouseEnter += (_, _) =>
        {
            button.Background = close ? new SolidColorBrush(Color.FromRgb(190, 49, 55)) : surface;
            if (button.Content is System.Windows.Shapes.Path icon) icon.Stroke = close ? Brushes.White : text;
        };
        button.MouseLeave += (_, _) =>
        {
            button.Background = Brushes.Transparent;
            if (button.Content is System.Windows.Shapes.Path icon) icon.Stroke = text;
        };
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        return button;
    }
    private void UpdateMaximizeButton()
    {
        if (maximizeButton == null) return;
        var maximized = WindowState == WindowState.Maximized;
        maximizeButton.Content = CaptionIcon(maximized ? "restore" : "maximize", text);
        maximizeButton.ToolTip = maximized ? "Відновити" : "Розгорнути";
    }
    private TextBox Box(string value = "", int width = 0)
    {
        var b = new TextBox { Text = value, Padding = Pad(10, 7), Margin = new Thickness(0, 5, 0, 12),
            Background = panel, Foreground = text, BorderBrush = line, CaretBrush = accent, FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center, MinHeight = 35 };
        if (width > 0) b.Width = width;
        return b;
    }

    private void BuildShell()
    {
        var root = new DockPanel { Background = bg };
        Content = root;
        var top = new Border { Background = panel, BorderBrush = line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(18, 10, 8, 10) };
        DockPanel.SetDock(top, Dock.Top);
        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        navButton = Btn("☰", (_, _) =>
        {
            if (inspectorOpen) inspectorOpen = false;
            sidebarOpen = !sidebarOpen;
            UpdateResponsiveLayout();
        });
        navButton.Width = 38; navButton.Padding = new Thickness(0); navButton.Margin = new Thickness(0, 0, 9, 0);
        navButton.ToolTip = "Показати кімнати та пристрої"; navButton.Visibility = Visibility.Collapsed;
        WindowChrome.SetIsHitTestVisibleInChrome(navButton, true);
        brand.Children.Add(navButton);
        var logo = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(9), Background = accent,
            Child = Symbol("computer", Brushes.White, 20) };
        brand.Children.Add(logo);
        brand.Children.Add(new TextBlock { Text = "  RemoteRooms", FontSize = 17, FontWeight = FontWeights.SemiBold,
            Foreground = text, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(brand, 0); topRow.Children.Add(brand);
        var topActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var more = Btn("⋯", (_, _) => { });
        more.Width = 38; more.Padding = new Thickness(0); more.ToolTip = "Додаткові дії";
        WindowChrome.SetIsHitTestVisibleInChrome(more, true);
        var moreMenu = new ContextMenu();
        var undo = new MenuItem { Header = "Скасувати   Ctrl+Z" }; undo.Click += (_, _) => { if (CoreBridge.Undo()) Refresh(); };
        var redo = new MenuItem { Header = "Повторити   Ctrl+Y" }; redo.Click += (_, _) => { if (CoreBridge.Redo()) Refresh(); };
        var export = new MenuItem { Header = "Експортувати адресну книгу" }; export.Click += (_, _) => Export();
        var theme = new MenuItem { Header = light ? "Темна тема" : "Світла тема" }; theme.Click += (_, _) => ToggleTheme();
        var fit = new MenuItem { Header = "Умістити план" }; fit.Click += (_, _) => { fitToWindow = true; FitPlan(true); };
        var roomProperties = new MenuItem { Header = "Властивості кімнати" };
        roomProperties.Click += (_, _) => { if (roomId != null) ShowRoomProperties(roomId); };
        moreMenu.Items.Add(undo); moreMenu.Items.Add(redo); moreMenu.Items.Add(new Separator());
        moreMenu.Items.Add(fit); moreMenu.Items.Add(roomProperties); moreMenu.Items.Add(new Separator());
        moreMenu.Items.Add(export); moreMenu.Items.Add(theme);
        more.ContextMenu = moreMenu;
        more.Click += (_, _) => moreMenu.IsOpen = true;
        topActions.Children.Add(more);
        var connect = Btn("＋  Підключення", (_, _) => QuickConnect(), true);
        WindowChrome.SetIsHitTestVisibleInChrome(connect, true);
        topActions.Children.Add(connect);
        Grid.SetColumn(topActions, 2); topRow.Children.Add(topActions);
        var captionActions = new StackPanel { Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        captionActions.Children.Add(CaptionButton("minimize", "Згорнути", (_, _) => WindowState = WindowState.Minimized));
        maximizeButton = CaptionButton("maximize", "Розгорнути", (_, _) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        captionActions.Children.Add(maximizeButton);
        captionActions.Children.Add(CaptionButton("close", "Закрити", (_, _) => Close(), true));
        Grid.SetColumn(captionActions, 3); topRow.Children.Add(captionActions);
        UpdateMaximizeButton();
        top.Child = topRow;
        root.Children.Add(top);

        var columns = new Grid();
        sidebarColumn = new ColumnDefinition { Width = new GridLength(238) };
        columns.ColumnDefinitions.Add(sidebarColumn);
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inspectorColumn = new ColumnDefinition { Width = new GridLength(inspectorOpen ? 292 : 0) };
        columns.ColumnDefinitions.Add(inspectorColumn);
        root.Children.Add(columns);

        sidebarPanel = new Border { Background = panel, BorderBrush = line, BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(15, 16, 14, 12) };
        Grid.SetColumn(sidebarPanel, 0); columns.Children.Add(sidebarPanel);
        var side = new DockPanel(); sidebarPanel.Child = side;
        search = Box(); search.Margin = new Thickness(0, 0, 0, 0); search.TextChanged += (_, _) => RenderSidebar();
        search.ToolTip = "Пошук за назвою ПК або адресою";
        var searchWrap = new StackPanel();
        var searchField = new Grid();
        searchField.Children.Add(search);
        var searchHint = Label("Пошук ПК або адреси", 12, false, muted);
        searchHint.Margin = new Thickness(12, 2, 0, 0); searchHint.IsHitTestVisible = false;
        searchField.Children.Add(searchHint);
        search.TextChanged += (_, _) => searchHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        searchWrap.Children.Add(searchField);
        searchWrap.Children.Add(new Border { Height = 18 });
        DockPanel.SetDock(searchWrap, Dock.Top); side.Children.Add(searchWrap);
        var sideScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        side.Children.Add(sideScroll);
        var lists = new StackPanel(); sideScroll.Content = lists;
        var roomsTitle = new DockPanel { Margin = new Thickness(0, 3, 0, 10), LastChildFill = false };
        roomsTitle.Children.Add(Label("Кімнати", 12, true, muted));
        var plus = Btn("＋", (_, _) => AddRoom()); plus.Padding = Pad(8, 2); plus.Margin = new Thickness(0); plus.MinHeight = 28;
        DockPanel.SetDock(plus, Dock.Right); roomsTitle.Children.Add(plus);
        lists.Children.Add(roomsTitle);
        roomsList = new StackPanel(); lists.Children.Add(roomsList);
        lists.Children.Add(new Border { BorderBrush = line, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 17, 0, 17) });
        lists.Children.Add(Label("Пристрої", 12, true, muted));
        addressList = new StackPanel { Margin = new Thickness(0, 9, 0, 0) }; lists.Children.Add(addressList);

        var middle = new DockPanel { Margin = new Thickness(19, 11, 19, 17) };
        Grid.SetColumn(middle, 1); columns.Children.Add(middle);
        var head = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleRow = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading = Label("План кімнати", 20, true); heading.TextTrimming = TextTrimming.CharacterEllipsis;
        titleRow.Children.Add(heading);
        subtitle = Label("", 11, false, muted); subtitle.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(subtitle, 1); titleRow.Children.Add(subtitle);
        head.Children.Add(titleRow);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(Btn("＋  ПК", (_, _) => AddComputer(), true));
        var zoomOut = Btn("−", (_, _) => { fitToWindow = false; roomView.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto; ChangeZoom(Math.Max(.3, zoom - .1), true); });
        zoomOut.ToolTip = "Зменшити масштаб"; tools.Children.Add(zoomOut);
        var zoomIn = Btn("＋", (_, _) => { fitToWindow = false; roomView.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto; ChangeZoom(Math.Min(2, zoom + .1), true); });
        zoomIn.ToolTip = "Збільшити масштаб"; zoomIn.Margin = new Thickness(0); tools.Children.Add(zoomIn);
        Grid.SetColumn(tools, 1); head.Children.Add(tools);
        DockPanel.SetDock(head, Dock.Top); middle.Children.Add(head);
        tabs = new TabControl { Background = panel, Foreground = text, BorderBrush = line, BorderThickness = new Thickness(1), Padding = new Thickness(0) };
        var tabStyle = new Style(typeof(TabItem));
        tabStyle.Setters.Add(new Setter(TabItem.FontSizeProperty, 12.5));
        tabStyle.Setters.Add(new Setter(TabItem.FontWeightProperty, FontWeights.SemiBold));
        tabStyle.Setters.Add(new Setter(TabItem.PaddingProperty, new Thickness(18, 10, 18, 10)));
        tabStyle.Setters.Add(new Setter(TabItem.ForegroundProperty, text));
        tabStyle.Setters.Add(new Setter(TabItem.BackgroundProperty, panel));
        var tabTemplate = new ControlTemplate(typeof(TabItem));
        var tabBorder = new FrameworkElementFactory(typeof(Border));
        tabBorder.Name = "TabBorder";
        tabBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(9, 9, 0, 0));
        tabBorder.SetValue(Border.BackgroundProperty, panel);
        tabBorder.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 2));
        tabBorder.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        var tabTitle = new FrameworkElementFactory(typeof(ContentPresenter));
        tabTitle.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        tabTitle.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(TabItem.PaddingProperty));
        tabTitle.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        tabBorder.AppendChild(tabTitle); tabTemplate.VisualTree = tabBorder;
        var selected = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BorderBrushProperty, accent, "TabBorder"));
        tabTemplate.Triggers.Add(selected);
        tabStyle.Setters.Add(new Setter(TabItem.TemplateProperty, tabTemplate));
        tabs.ItemContainerStyle = tabStyle;
        planTab = new TabItem { Header = "План кімнати" };
        roomView = new ScrollViewer { Background = surface, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12) };
        plan = new Canvas { Width = 900, Height = 600, Background = PlanBackground(), LayoutTransform = new ScaleTransform(zoom, zoom) };
        plan.MouseLeftButtonDown += (_, e) =>
        {
            if (inspectorOpen && ReferenceEquals(e.OriginalSource, plan))
            { inspectorOpen = false; UpdateInspector(); }
        };
        var planFrame = new Border { Background = panel, BorderBrush = line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(0), HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Child = plan };
        roomView.Content = planFrame; planTab.Content = roomView;
        roomView.SizeChanged += (_, _) => { if (fitToWindow) FitPlan(); };
        tabs.Items.Add(planTab); tabs.SelectedItem = planTab;
        middle.Children.Add(tabs);

        inspectorPanel = new Border { Background = panel, BorderBrush = line, BorderThickness = new Thickness(1, 0, 0, 0),
            Visibility = inspectorOpen ? Visibility.Visible : Visibility.Collapsed };
        Grid.SetZIndex(inspectorPanel, 5);
        Grid.SetColumn(inspectorPanel, 2); columns.Children.Add(inspectorPanel);
        var inspectorRoot = new DockPanel(); inspectorPanel.Child = inspectorRoot;
        var inspectorHead = new Border { BorderBrush = line, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 13, 12, 13), Background = panel };
        DockPanel.SetDock(inspectorHead, Dock.Top); inspectorRoot.Children.Add(inspectorHead);
        var inspectorHeadRow = new DockPanel(); inspectorHead.Child = inspectorHeadRow;
        inspectorTitle = Label("Властивості", 16, true);
        var closeInspector = Btn("×", (_, _) => { inspectorOpen = false; UpdateInspector(); });
        closeInspector.Width = 29; closeInspector.MinHeight = 29; closeInspector.Padding = new Thickness(0);
        closeInspector.Margin = new Thickness(0); closeInspector.ToolTip = "Закрити властивості (Esc)";
        DockPanel.SetDock(closeInspector, Dock.Right); inspectorHeadRow.Children.Add(closeInspector);
        inspectorHeadRow.Children.Add(inspectorTitle);
        inspectorScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(16, 15, 15, 16) };
        inspectorRoot.Children.Add(inspectorScroll);
        details = new StackPanel(); inspectorScroll.Content = details;
        UpdateResponsiveLayout();
    }
    private void UpdateResponsiveLayout()
    {
        if (sidebarColumn == null || sidebarPanel == null || inspectorColumn == null || inspectorPanel == null) return;
        var sidebarWasVisible = sidebarPanel.Visibility == Visibility.Visible;
        var inspectorWasVisible = inspectorPanel.Visibility == Visibility.Visible;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var compact = width < 1100;
        var veryCompact = width < 900;
        var showSidebar = veryCompact ? sidebarOpen && !inspectorOpen : !(compact && inspectorOpen);
        navButton.Visibility = showSidebar ? Visibility.Collapsed : Visibility.Visible;
        sidebarColumn.Width = new GridLength(showSidebar ? (width < 940 ? 216 : 238) : 0);
        sidebarPanel.Visibility = showSidebar ? Visibility.Visible : Visibility.Collapsed;
        inspectorColumn.Width = new GridLength(inspectorOpen && !compact ? 294 : 0);
        Grid.SetColumn(inspectorPanel, compact ? 1 : 2);
        inspectorPanel.Width = compact ? Math.Min(304, Math.Max(250, width * .4)) : double.NaN;
        inspectorPanel.HorizontalAlignment = compact ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Stretch;
        inspectorPanel.Margin = compact ? new Thickness(0, 95, 8, 8) : new Thickness(0);
        inspectorPanel.CornerRadius = compact ? new CornerRadius(12) : new CornerRadius(0);
        inspectorPanel.BorderThickness = compact ? new Thickness(1) : new Thickness(1, 0, 0, 0);
        inspectorPanel.Visibility = inspectorOpen ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded && showSidebar && !sidebarWasVisible)
            Dispatcher.BeginInvoke(() => { if (sidebarPanel.Visibility == Visibility.Visible) Motion.Reveal(sidebarPanel, -10, 0, 170, .35); },
                System.Windows.Threading.DispatcherPriority.Loaded);
        if (IsLoaded && inspectorOpen && !inspectorWasVisible)
            Dispatcher.BeginInvoke(() => { if (inspectorPanel.Visibility == Visibility.Visible) Motion.Reveal(inspectorPanel, 10, 0, 170, .35); },
                System.Windows.Threading.DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(() => FitPlan(), System.Windows.Threading.DispatcherPriority.Loaded);
    }
    internal void SetPreviewSize(double width, double height)
    {
        Width = width; Height = height;
        UpdateResponsiveLayout();
    }
    private void UpdateInspector()
    {
        if (inspectorColumn == null || inspectorPanel == null) return;
        UpdateResponsiveLayout();
        if (inspectorOpen) RenderDetails();
    }
    private void ToggleTheme()
    {
        var selectedTab = tabs.SelectedItem as TabItem;
        var query = search.Text;
        var preserved = sessions.Keys.ToList();
        foreach (var item in preserved) tabs.Items.Remove(item);
        light = !light; SetTheme(); BuildShell();
        foreach (var item in preserved) { sessions[item].ApplyTheme(light); tabs.Items.Add(item); }
        foreach (var host in hostedSessions) host.View.ApplyTheme(light);
        search.Text = query;
        Refresh();
        if (selectedTab != null && selectedTab != planTab && tabs.Items.Contains(selectedTab)) tabs.SelectedItem = selectedTab;
    }

    private void Refresh()
    {
        state = CoreBridge.State();
        if (roomId == null || state.Rooms.All(r => r.Id != roomId)) roomId = state.Rooms.FirstOrDefault()?.Id;
        if (computerId != null && state.Computers.All(c => c.Id != computerId)) computerId = null;
        if (deskId != null && state.Desks.All(d => d.Id != deskId)) deskId = null;
        RenderSidebar(); FitPlan(); RenderPlan(); RenderDetails();
    }
    private void FitPlan(bool animate = false)
    {
        if (!fitToWindow || roomView == null) return;
        roomView.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        var room = state.Rooms.FirstOrDefault(r => r.Id == roomId);
        if (room == null || roomView.ActualWidth < 100 || roomView.ActualHeight < 100) return;
        var target = Math.Clamp(Math.Min((roomView.ActualWidth - 32) / room.Width,
            (roomView.ActualHeight - 32) / room.Height), .3, 1.1);
        if (Math.Abs(target - zoom) > .01) ChangeZoom(target, animate);
    }
    private void ChangeZoom(double target, bool animate)
    {
        zoom = target;
        if (plan.LayoutTransform is ScaleTransform scale) Motion.Zoom(scale, target, animate);
        else plan.LayoutTransform = new ScaleTransform(target, target);
    }
    private void Edit(object command)
    {
        try { CoreBridge.Apply(command); Refresh(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Не вдалося зберегти", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void RenderSidebar()
    {
        if (roomsList == null) return;
        roomsList.Children.Clear(); addressList.Children.Clear();
        foreach (var room in state.Rooms)
        {
            var count = state.Computers.Count(c => c.RoomId == room.Id);
            bool selected = room.Id == roomId;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new Border { Width = 29, Height = 29, CornerRadius = new CornerRadius(7), Background = selected ? accent : surface,
                Child = Symbol("room", selected ? Brushes.White : accent, 16) };
            row.Children.Add(icon);
            var title = Label(room.Name, 13, selected, text); title.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(title, 1); row.Children.Add(title);
            var badge = Label(count.ToString(), 11, false, muted); Grid.SetColumn(badge, 2); row.Children.Add(badge);
            var tile = new Border { Child = row, Background = selected ? surface : Brushes.Transparent, CornerRadius = new CornerRadius(9),
                Padding = new Thickness(9, 7, 11, 7), Margin = new Thickness(0, 0, 0, 4), Cursor = Cursors.Hand };
            tile.MouseLeftButtonUp += (_, _) => SelectRoom(room.Id);
            tile.Focusable = true;
            tile.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { SelectRoom(room.Id); e.Handled = true; } };
            tile.GotKeyboardFocus += (_, _) => tile.BorderBrush = accent;
            tile.LostKeyboardFocus += (_, _) => tile.BorderBrush = Brushes.Transparent;
            tile.BorderThickness = new Thickness(1);
            tile.MouseEnter += (_, _) => { if (room.Id != roomId) tile.Background = surface; };
            tile.MouseLeave += (_, _) => { if (room.Id != roomId) tile.Background = Brushes.Transparent; };
            var roomMenu = new ContextMenu();
            var properties = new MenuItem { Header = "Властивості кімнати" };
            properties.Click += (_, _) => ShowRoomProperties(room.Id);
            roomMenu.Items.Add(properties); tile.ContextMenu = roomMenu;
            tile.ToolTip = "Правий клік — властивості кімнати";
            roomsList.Children.Add(tile);
        }
        if (state.Rooms.Count == 0) roomsList.Children.Add(Label("Додайте першу кімнату", 12, false, muted));
        var term = search.Text.Trim();
        foreach (var pc in state.Computers.Where(c => term.Length == 0 || c.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
            state.Endpoints.Any(e => e.ComputerId == c.Id && e.Host.Contains(term, StringComparison.OrdinalIgnoreCase))))
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = surface,
                Child = Symbol("computer", accent, 17) };
            row.Children.Add(icon);
            var names = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
            names.Children.Add(Label(pc.Name, 13, true));
            var protocols = string.Join("  ·  ", state.Endpoints.Where(e => e.ComputerId == pc.Id).Select(e => e.Protocol));
            names.Children.Add(Label(protocols.Length == 0 ? "Немає підключення" : protocols, 11, false, muted));
            Grid.SetColumn(names, 1); row.Children.Add(names);
            var tile = new Border { Child = row, Padding = new Thickness(8, 7, 8, 7), Margin = new Thickness(0, 0, 0, 3),
                CornerRadius = new CornerRadius(9), Background = pc.Id == computerId ? surface : Brushes.Transparent, Cursor = Cursors.Hand };
            tile.MouseLeftButtonUp += (_, e) =>
            {
                if (e.ClickCount == 2)
                {
                    var ep = state.Endpoints.FirstOrDefault(a => a.ComputerId == pc.Id);
                    if (ep != null) OpenSession(ep);
                    return;
                }
                SelectComputer(pc.Id);
            };
            tile.Focusable = true; tile.BorderThickness = new Thickness(1);
            tile.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { SelectComputer(pc.Id); e.Handled = true; } };
            tile.GotKeyboardFocus += (_, _) => tile.BorderBrush = accent;
            tile.LostKeyboardFocus += (_, _) => tile.BorderBrush = Brushes.Transparent;
            tile.MouseEnter += (_, _) => { if (pc.Id != computerId) tile.Background = surface; };
            tile.MouseLeave += (_, _) => { if (pc.Id != computerId) tile.Background = Brushes.Transparent; };
            addressList.Children.Add(tile);
        }
    }
    private void SelectRoom(string id)
    {
        roomId = id; computerId = null; deskId = null; inspectorOpen = false; UpdateInspector();
        tabs.SelectedItem = planTab; RenderSidebar(); FitPlan(); RenderPlan(); RenderDetails();
        Motion.Reveal(roomView, 0, 6, 170, .55);
    }
    private void ShowRoomProperties(string id)
    {
        SelectRoom(id);
        inspectorOpen = true; UpdateInspector(); RenderDetails();
        inspectorScroll.ScrollToTop();
    }
    private void SelectComputer(string id)
    {
        var pc = state.Computers.FirstOrDefault(c => c.Id == id);
        if (pc == null) return;
        var wasOpen = inspectorOpen;
        roomId = pc.RoomId; computerId = id; deskId = null; inspectorOpen = true; UpdateInspector();
        tabs.SelectedItem = planTab; RenderSidebar(); FitPlan(); RenderPlan(); RenderDetails();
        inspectorScroll.ScrollToTop();
        if (wasOpen) Motion.Reveal(details, 0, 6, 160, .5);
    }
    private void SelectDesk(string id)
    {
        var desk = state.Desks.FirstOrDefault(d => d.Id == id);
        if (desk == null) return;
        var wasOpen = inspectorOpen;
        roomId = desk.RoomId; deskId = id; computerId = null; inspectorOpen = true; UpdateInspector();
        tabs.SelectedItem = planTab; RenderSidebar(); FitPlan(); RenderPlan(); RenderDetails();
        inspectorScroll.ScrollToTop();
        if (wasOpen) Motion.Reveal(details, 0, 6, 160, .5);
    }

    private void RenderPlan()
    {
        if (plan == null) return;
        plan.Children.Clear();
        var room = state.Rooms.FirstOrDefault(r => r.Id == roomId);
        heading.Text = room?.Name ?? "План кімнати";
        subtitle.Text = room == null ? "" :
            $"{state.Computers.Count(c => c.RoomId == roomId)} ПК";
        if (room == null)
        {
            plan.Width = 900; plan.Height = 600;
            var empty = new StackPanel { Width = 420, VerticalAlignment = VerticalAlignment.Center };
            empty.Children.Add(Symbol("room", accent, 42));
            empty.Children.Add(new TextBlock { Text = "Створіть першу кімнату", FontSize = 22, FontWeight = FontWeights.SemiBold,
                Foreground = text, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 18, 0, 8) });
            empty.Children.Add(new TextBlock { Text = "Розмістіть комп’ютери на плані, щоб швидко відкривати підключення.",
                FontSize = 14, Foreground = muted, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
            var create = Btn("＋  Створити кімнату", (_, _) => AddRoom(), true);
            create.Margin = new Thickness(110, 23, 110, 0); empty.Children.Add(create);
            Canvas.SetLeft(empty, 240); Canvas.SetTop(empty, 180); plan.Children.Add(empty);
            return;
        }
        plan.Width = room.Width; plan.Height = room.Height;
        plan.LayoutTransform = new ScaleTransform(zoom, zoom);
        var computers = state.Computers.Where(c => c.RoomId == roomId).ToList();
        if (computers.Count == 0)
        {
            var empty = new StackPanel { Width = Math.Min(300, room.Width - 40) };
            empty.Children.Add(new TextBlock { Text = "У кімнаті ще немає ПК", FontSize = 18,
                FontWeight = FontWeights.SemiBold, Foreground = text, TextAlignment = TextAlignment.Center });
            empty.Children.Add(new TextBlock { Text = "Додайте ПК, щоб розмістити його на плані й зберегти адресу підключення.",
                FontSize = 12, Foreground = muted, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 16) });
            var add = Btn("＋  Додати ПК", (_, _) => AddComputer(), true);
            add.HorizontalAlignment = System.Windows.HorizontalAlignment.Center; add.Margin = new Thickness(0);
            empty.Children.Add(add);
            Canvas.SetLeft(empty, (room.Width - empty.Width) / 2);
            Canvas.SetTop(empty, Math.Max(40, (room.Height - 120) / 2));
            plan.Children.Add(empty);
        }
        foreach (var pc in computers) AddPlanItem(pc.Id, "computers", pc.Name, pc.X, pc.Y, PcWidth, PcHeight,
            pc.Id == computerId ? new SolidColorBrush(light ? Color.FromRgb(226, 246, 241) : Color.FromRgb(28, 79, 75)) : panel, true);
    }

    private void AddPlanItem(string id, string collection, string label, double x, double y, double width, double height, Brush fill, bool pc)
    {
        var selected = pc ? id == computerId : id == deskId;
        var inside = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(11, 0, 8, 0) };
        inside.Children.Add(new Border { Width = 27, Height = 27, CornerRadius = new CornerRadius(7),
            Background = pc ? surface : panel, Child = Symbol(pc ? "computer" : "desk", pc && selected && !light ? Brushes.White : pc ? accent : muted, 16) });
        var itemText = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        itemText.Children.Add(new TextBlock { Text = label, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            Foreground = text, MaxWidth = width - 54, TextTrimming = TextTrimming.CharacterEllipsis });
        if (pc)
        {
            var protocol = state.Endpoints.FirstOrDefault(e => e.ComputerId == id)?.Protocol ?? "Без адреси";
            itemText.Children.Add(new TextBlock { Text = protocol, FontSize = 10.5, Foreground = muted,
                MaxWidth = width - 54, TextTrimming = TextTrimming.CharacterEllipsis });
        }
        inside.Children.Add(itemText);
        var border = new Border { Width = width, Height = height, Background = fill, BorderBrush = selected ? accent : line,
            BorderThickness = new Thickness(selected ? 2 : 1), CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand,
            Child = inside, ToolTip = "Подвійний клік — відкрити · Правий клік — дії", Focusable = true, Tag = id,
            RenderTransform = new ScaleTransform(1, 1), RenderTransformOrigin = new Point(.5, .5) };
        Canvas.SetLeft(border, x); Canvas.SetTop(border, y);
        plan.Children.Add(border);
        border.MouseEnter += (_, _) => { if (!border.IsMouseCaptured) Motion.Scale(border, 1.025); };
        border.MouseLeave += (_, _) => Motion.Scale(border, 1);
        Point start = default; double left = 0, top = 0; bool dragged = false;
        border.MouseLeftButtonDown += (_, e) =>
        {
            if (pc && e.ClickCount == 2)
            {
                var endpoint = state.Endpoints.FirstOrDefault(a => a.ComputerId == id);
                if (endpoint != null) OpenSession(endpoint);
                e.Handled = true;
                return;
            }
            Motion.Scale(border, 1, 80);
            start = e.GetPosition(plan); left = Canvas.GetLeft(border); top = Canvas.GetTop(border); dragged = false;
            border.CaptureMouse(); e.Handled = true;
        };
        border.MouseMove += (_, e) =>
        {
            if (!border.IsMouseCaptured) return;
            var p = e.GetPosition(plan);
            if (Math.Abs(p.X - start.X) + Math.Abs(p.Y - start.Y) > 3) dragged = true;
            if (!dragged) return;
            Canvas.SetLeft(border, Math.Clamp(Math.Round((left + p.X - start.X) / 25) * 25, 0, plan.Width - width));
            Canvas.SetTop(border, Math.Clamp(Math.Round((top + p.Y - start.Y) / 25) * 25, 0, plan.Height - height));
        };
        border.MouseLeftButtonUp += (_, e) =>
        {
            if (!border.IsMouseCaptured) return;
            border.ReleaseMouseCapture();
            if (dragged) Edit(new { op = "move", collection, id, x = Canvas.GetLeft(border), y = Canvas.GetTop(border) });
            else if (pc) SelectComputer(id);
            else SelectDesk(id);
            if (!dragged) Dispatcher.BeginInvoke(() =>
                plan.Children.OfType<Border>().FirstOrDefault(item => Equals(item.Tag, id))?.Focus(),
                System.Windows.Threading.DispatcherPriority.Input);
            e.Handled = true;
        };
        border.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && pc)
            {
                var endpoint = state.Endpoints.FirstOrDefault(a => a.ComputerId == id);
                if (endpoint != null) OpenSession(endpoint);
                else SelectComputer(id);
                e.Handled = true;
            }
            else if (e.Key == Key.Space) { if (pc) SelectComputer(id); else SelectDesk(id); e.Handled = true; }
        };
        var menu = new ContextMenu();
        if (pc)
        {
            var open = new MenuItem { Header = "Відкрити підключення" };
            open.Click += (_, _) =>
            {
                var endpoint = state.Endpoints.FirstOrDefault(a => a.ComputerId == id);
                if (endpoint != null) OpenSession(endpoint);
            };
            menu.Items.Add(open);
            menu.Items.Add(new Separator());
        }
        var delete = new MenuItem { Header = pc ? "Видалити ПК" : "Видалити стіл" };
        delete.Click += (_, _) => { if (pc) DeleteComputer(id); else DeleteDesk(id); };
        menu.Items.Add(delete);
        border.ContextMenu = menu;
    }

    private void RenderDetails()
    {
        details.Children.Clear();
        var pc = state.Computers.FirstOrDefault(c => c.Id == computerId);
        if (pc != null) { inspectorTitle.Text = "Комп’ютер"; RenderComputerDetails(pc); }
        else if (state.Desks.FirstOrDefault(d => d.Id == deskId) is { } desk)
        { inspectorTitle.Text = "Стіл"; RenderDeskDetails(desk); }
        else if (state.Rooms.FirstOrDefault(r => r.Id == roomId) is { } room)
        { inspectorTitle.Text = "Кімната"; RenderRoomDetails(room); }
        else
        {
            inspectorTitle.Text = "Властивості";
            details.Children.Add(new TextBlock { Text = "◫", FontSize = 46, Foreground = accent, Margin = new Thickness(0, 12, 0, 9) });
            details.Children.Add(Label("Поки порожньо", 16, true));
            var hint = Label("Виберіть кімнату або комп’ютер, щоб побачити його параметри.", 12, false, muted);
            hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 8, 0, 20); details.Children.Add(hint);
        }
    }

    private void RenderRoomDetails(Room room)
    {
        details.Children.Add(Label(room.Name, 19, true));
        var count = Label($"ПК: {state.Computers.Count(c => c.RoomId == room.Id)}", 12, false, muted);
        count.Margin = new Thickness(0, 3, 0, 20); details.Children.Add(count);
        details.Children.Add(Label("Назва кімнати", 12, true, muted));
        var name = Box(room.Name); details.Children.Add(name);
        var save = Btn("Зберегти назву", (_, _) => Edit(new { op = "set", collection = "rooms", id = room.Id, field = "name", value = name.Text }));
        save.Margin = new Thickness(0); save.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch; details.Children.Add(save);
        details.Children.Add(new Border { Height = 21 });
        details.Children.Add(Label($"План: {room.Width:0} × {room.Height:0}", 12, false, muted));
        details.Children.Add(new Border { Height = 21 });
        details.Children.Add(DangerButton("Видалити кімнату", (_, _) =>
        {
            if (MessageBox.Show("Видалити кімнату разом з усіма її об’єктами та підключеннями?", "Підтвердження", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                var ids = state.Computers.Where(c => c.RoomId == room.Id).Select(c => c.Id).ToHashSet();
                var credentials = state.Endpoints.Where(e => ids.Contains(e.ComputerId)).Select(e => e.Id).ToList();
                Edit(new { op = "delete", collection = "rooms", id = room.Id });
                if (state.Rooms.All(r => r.Id != room.Id)) foreach (var id in credentials) Credentials.Delete(id);
            }
        }));
    }

    private void RenderDeskDetails(Desk desk)
    {
        var title = Label(desk.Name, 19, true); title.Margin = new Thickness(0, 0, 0, 20);
        details.Children.Add(title);
        details.Children.Add(Label("Назва столу", 12, true, muted));
        var name = Box(desk.Name); details.Children.Add(name);
        var save = Btn("Зберегти назву", (_, _) => Edit(new { op = "set", collection = "desks", id = desk.Id, field = "name", value = name.Text }));
        save.Margin = new Thickness(0); details.Children.Add(save);
        details.Children.Add(new Border { Height = 21 });
        details.Children.Add(DangerButton("Видалити стіл", (_, _) => DeleteDesk(desk.Id)));
    }

    private void RenderComputerDetails(Computer pc)
    {
        var endpoints = state.Endpoints.Where(e => e.ComputerId == pc.Id).ToList();
        var identity = new Grid { Margin = new Thickness(0, 0, 0, 17) };
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var identityText = new StackPanel();
        var pcName = Label(pc.Name, 18, true); pcName.TextTrimming = TextTrimming.CharacterEllipsis;
        identityText.Children.Add(pcName);
        var count = Label($"{endpoints.Count} підключень", 11, false, muted);
        count.Margin = new Thickness(0, 3, 0, 0); identityText.Children.Add(count);
        identity.Children.Add(identityText);
        var computerActions = Btn("⋯", (_, _) => { });
        computerActions.Width = 34; computerActions.Padding = new Thickness(0);
        computerActions.Margin = new Thickness(0); computerActions.ToolTip = "Дії з ПК";
        Grid.SetColumn(computerActions, 1); identity.Children.Add(computerActions);
        details.Children.Add(identity);
        var renameForm = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 17) };
        renameForm.Children.Add(Label("Назва ПК", 12, true, muted));
        var name = Box(pc.Name); renameForm.Children.Add(name);
        var save = Btn("Зберегти назву", (_, _) => Edit(new { op = "set", collection = "computers", id = pc.Id, field = "name", value = name.Text.Trim() }));
        save.Margin = new Thickness(0); save.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        renameForm.Children.Add(save); details.Children.Add(renameForm);
        var computerMenu = new ContextMenu();
        var rename = new MenuItem { Header = "Змінити назву" };
        rename.Click += (_, _) => { renameForm.Visibility = Visibility.Visible; name.Focus(); name.SelectAll(); };
        var deleteComputer = new MenuItem { Header = "Видалити ПК" };
        deleteComputer.Click += (_, _) => DeleteComputer(pc.Id);
        computerMenu.Items.Add(rename); computerMenu.Items.Add(new Separator()); computerMenu.Items.Add(deleteComputer);
        computerActions.ContextMenu = computerMenu;
        computerActions.Click += (_, _) => computerMenu.IsOpen = true;
        var connectionsTitle = Label("Підключення", 13, true);
        connectionsTitle.Margin = new Thickness(0, 0, 0, 6); details.Children.Add(connectionsTitle);
        foreach (var ep in endpoints)
        {
            var card = new StackPanel();
            card.Children.Add(Label(ep.Protocol, 11, true, accent));
            var endpointLabel = Label($"{ep.Host}:{ep.Port}", 13, true); endpointLabel.TextTrimming = TextTrimming.CharacterEllipsis;
            card.Children.Add(endpointLabel);
            if (!string.IsNullOrWhiteSpace(ep.Username)) card.Children.Add(Label(ep.Username, 11, false, muted));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var open = Btn("Відкрити", (_, _) => OpenSession(ep), true);
            open.ToolTip = "Відкрити віддалений доступ на весь екран"; buttons.Children.Add(open);
            var endpointActions = Btn("⋯", (_, _) => { });
            endpointActions.ToolTip = "Дії з підключенням"; endpointActions.Width = 34;
            endpointActions.Padding = new Thickness(0); endpointActions.Margin = new Thickness(0);
            var endpointMenu = new ContextMenu();
            var password = new MenuItem { Header = "Змінити пароль" }; password.Click += (_, _) => PasswordDialog(ep);
            var removeEndpoint = new MenuItem { Header = "Видалити підключення" };
            removeEndpoint.Click += (_, _) =>
            {
                if (MessageBox.Show("Видалити це підключення?", "Підтвердження", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                { Credentials.Delete(ep.Id); Edit(new { op = "delete", collection = "endpoints", id = ep.Id }); }
            };
            endpointMenu.Items.Add(password); endpointMenu.Items.Add(new Separator()); endpointMenu.Items.Add(removeEndpoint);
            endpointActions.ContextMenu = endpointMenu;
            endpointActions.Click += (_, _) => endpointMenu.IsOpen = true;
            buttons.Children.Add(endpointActions);
            card.Children.Add(buttons);
            details.Children.Add(new Border { Child = card, Background = surface, BorderBrush = line,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9),
                Padding = new Thickness(11), Margin = new Thickness(0, 7, 0, 0) });
        }
        if (endpoints.Count == 0)
        {
            var empty = Label("Додайте адресу, щоб відкрити цей ПК.", 12, false, muted);
            empty.TextWrapping = TextWrapping.Wrap;
            empty.Margin = new Thickness(0, 4, 0, 7); details.Children.Add(empty);
        }
        details.Children.Add(new Border { Height = 10 });
        var addForm = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
        var toggleForm = Btn("＋  Додати підключення", (_, _) =>
        {
            addForm.Visibility = addForm.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (addForm.Visibility == Visibility.Visible)
                Dispatcher.BeginInvoke(() =>
                {
                    Motion.Reveal(addForm, 0, 8, 180);
                    addForm.BringIntoView();
                }, System.Windows.Threading.DispatcherPriority.Loaded);
        });
        toggleForm.Margin = new Thickness(0); toggleForm.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        toggleForm.Click += (_, _) => toggleForm.Content = addForm.Visibility == Visibility.Visible
            ? "−  Сховати форму" : "＋  Додати підключення";
        details.Children.Add(toggleForm);
        var protocol = new ComboBox { Margin = new Thickness(0, 0, 0, 12), Padding = Pad(8, 5),
            Background = panel, Foreground = text, BorderBrush = line,
            ItemsSource = new[] { "RDP", "VNC", "SSH" }, SelectedIndex = 0 };
        addForm.Children.Add(Label("Протокол", 12, true, muted)); addForm.Children.Add(protocol);
        addForm.Children.Add(Label("Адреса або IP", 12, true, muted)); var host = Box(); addForm.Children.Add(host);
        addForm.Children.Add(Label("Порт", 12, true, muted)); var port = Box("3389"); addForm.Children.Add(port);
        protocol.SelectionChanged += (_, _) => port.Text = (string)protocol.SelectedItem == "RDP" ? "3389" : (string)protocol.SelectedItem == "VNC" ? "5900" : "22";
        addForm.Children.Add(Label("Користувач", 12, true, muted)); var user = Box(); addForm.Children.Add(user);
        addForm.Children.Add(Label("Пароль (необов’язково)", 12, true, muted));
        var passwordBox = new PasswordBox { Padding = Pad(9, 7), Margin = new Thickness(0, 5, 0, 13),
            Background = panel, Foreground = text, BorderBrush = line };
        addForm.Children.Add(passwordBox);
        addForm.Children.Add(Btn("Зберегти підключення", (_, _) =>
        {
            if (!int.TryParse(port.Text, out var p) || p is < 1 or > 65535) { MessageBox.Show("Укажіть коректний порт."); return; }
            if (string.IsNullOrWhiteSpace(host.Text)) { MessageBox.Show("Укажіть адресу."); return; }
            var ids = state.Endpoints.Select(x => x.Id).ToHashSet();
            Edit(new { op = "addEndpoint", computerId = pc.Id, protocol = (string)protocol.SelectedItem, host = host.Text.Trim(), port = p, username = user.Text.Trim() });
            var added = state.Endpoints.FirstOrDefault(x => !ids.Contains(x.Id));
            if (added != null && passwordBox.Password.Length > 0) Credentials.Save(added.Id, user.Text.Trim(), passwordBox.Password);
        }, true));
        details.Children.Add(addForm);
    }

    private void DeleteDesk(string id)
    {
        var desk = state.Desks.FirstOrDefault(d => d.Id == id);
        if (desk == null) return;
        if (MessageBox.Show($"Видалити стіл «{desk.Name}» з плану кімнати?", "Підтвердження",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Edit(new { op = "delete", collection = "desks", id });
    }

    private void DeleteComputer(string id)
    {
        var pc = state.Computers.FirstOrDefault(c => c.Id == id);
        if (pc == null) return;
        var endpoints = state.Endpoints.Where(e => e.ComputerId == id).Select(e => e.Id).ToArray();
        var message = endpoints.Length == 0
            ? $"Видалити ПК «{pc.Name}» з плану кімнати?"
            : $"Видалити ПК «{pc.Name}» разом із {endpoints.Length} підключеннями та збереженими паролями?";
        if (MessageBox.Show(message, "Підтвердження", MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            CoreBridge.Apply(new { op = "delete", collection = "computers", id });
            foreach (var endpointId in endpoints) Credentials.Delete(endpointId);
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Не вдалося видалити ПК", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddRoom()
    {
        var name = Prompt("Нова кімната", "Назва кімнати", "Кімната " + (state.Rooms.Count + 1));
        if (name == null) return;
        var existing = state.Rooms.Select(r => r.Id).ToHashSet();
        Edit(new { op = "addRoom", name });
        var created = state.Rooms.FirstOrDefault(r => !existing.Contains(r.Id));
        if (created == null) return;
        roomId = created.Id; Refresh();
        Motion.Reveal(roomView, 0, 6, 180, .55);
    }
    private void AddDesk()
    {
        if (roomId == null) { MessageBox.Show("Спочатку додайте кімнату."); return; }
        var existing = state.Desks.Select(d => d.Id).ToHashSet();
        var p = FreePosition(roomId, DeskWidth, DeskHeight);
        Edit(new { op = "addDesk", roomId, x = p.X, y = p.Y, name = "Стіл " + (state.Desks.Count(d => d.RoomId == roomId) + 1) });
        var created = state.Desks.FirstOrDefault(d => !existing.Contains(d.Id));
        if (created != null) RevealPlanItem(created.Id);
    }
    private void AddComputer()
    {
        if (roomId == null) { MessageBox.Show("Спочатку додайте кімнату."); return; }
        var existing = state.Computers.Select(c => c.Id).ToHashSet();
        var p = FreePosition(roomId, PcWidth, PcHeight);
        Edit(new { op = "addComputer", roomId, x = p.X, y = p.Y, name = "ПК " + (state.Computers.Count(c => c.RoomId == roomId) + 1) });
        var created = state.Computers.FirstOrDefault(c => !existing.Contains(c.Id));
        if (created == null) return;
        computerId = created.Id; deskId = null; inspectorOpen = true; UpdateInspector(); Refresh();
        RevealPlanItem(created.Id);
    }
    private void RevealPlanItem(string id) => Dispatcher.BeginInvoke(() =>
    {
        var tile = plan.Children.OfType<Border>().FirstOrDefault(item => Equals(item.Tag, id));
        if (tile != null) Motion.Reveal(tile, milliseconds: 190);
    }, System.Windows.Threading.DispatcherPriority.Loaded);
    private Point FreePosition(string targetRoomId, double width, double height)
    {
        var room = state.Rooms.First(r => r.Id == targetRoomId);
        var occupied = state.Computers.Where(c => c.RoomId == targetRoomId)
            .Select(c => new Rect(c.X, c.Y, PcWidth, PcHeight)).ToList();
        for (double y = 75; y + height < room.Height - 25; y += 25)
            for (double x = 75; x + width < room.Width - 25; x += 25)
            {
                var candidate = new Rect(x - 12, y - 12, width + 24, height + 24);
                if (occupied.All(item => !item.IntersectsWith(candidate))) return new Point(x, y);
            }
        return new Point(75, 75);
    }
    private void QuickConnect()
    {
        var dialog = new Window { Title = "Нове підключення", Width = 420, Height = 655, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = panel, ResizeMode = ResizeMode.NoResize };
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(Label("Нове підключення", 22, true));
        var intro = Label("Збережіть пристрій у кімнаті та відкрийте сеанс.", 12, false, muted);
        intro.Margin = new Thickness(0, 5, 0, 18); body.Children.Add(intro);
        body.Children.Add(Label("Протокол", 12, true, muted));
        var protocol = new ComboBox { ItemsSource = new[] { "RDP", "VNC", "SSH" }, SelectedIndex = 0,
            Margin = new Thickness(0, 6, 0, 13), Padding = Pad(8, 6) };
        body.Children.Add(protocol);
        body.Children.Add(Label("Назва пристрою", 12, true, muted));
        var deviceName = Box("Новий ПК"); body.Children.Add(deviceName);
        body.Children.Add(Label("Адреса або IP", 12, true, muted));
        var host = Box(); body.Children.Add(host);
        var networkRow = new Grid();
        networkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        networkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var portSection = new StackPanel(); portSection.Children.Add(Label("Порт", 12, true, muted));
        var port = Box("3389"); port.Margin = new Thickness(0, 6, 0, 13); portSection.Children.Add(port);
        networkRow.Children.Add(portSection);
        var userSection = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        userSection.Children.Add(Label("Користувач", 12, true, muted));
        var user = Box(); userSection.Children.Add(user); Grid.SetColumn(userSection, 1); networkRow.Children.Add(userSection);
        body.Children.Add(networkRow);
        protocol.SelectionChanged += (_, _) => port.Text = (string)protocol.SelectedItem == "RDP" ? "3389" : (string)protocol.SelectedItem == "VNC" ? "5900" : "22";
        body.Children.Add(Label("Пароль (необов’язково)", 12, true, muted));
        var password = new PasswordBox { Padding = Pad(10, 8), Margin = new Thickness(0, 6, 0, 13),
            Background = surface, Foreground = text, BorderBrush = line };
        body.Children.Add(password);
        ComboBox? roomChoice = null;
        if (state.Rooms.Count > 0)
        {
            body.Children.Add(Label("Кімната", 12, true, muted));
            roomChoice = new ComboBox { ItemsSource = state.Rooms, DisplayMemberPath = "Name", SelectedValuePath = "Id",
                SelectedValue = roomId, Margin = new Thickness(0, 6, 0, 17), Padding = Pad(8, 6) };
            body.Children.Add(roomChoice);
        }
        else
        {
            var hint = Label("Буде створено кімнату «Швидкі підключення».", 12, false, muted);
            hint.Margin = new Thickness(0, 1, 0, 17); body.Children.Add(hint);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        actions.Children.Add(Btn("Скасувати", (_, _) => dialog.Close()));
        Endpoint? created = null;
        actions.Children.Add(Btn("Зберегти й підключитися", (_, _) =>
        {
            if (deviceName.Text.Trim().Length == 0 || host.Text.Trim().Length == 0)
            { MessageBox.Show("Укажіть назву пристрою та адресу.", "Нове підключення"); return; }
            if (!int.TryParse(port.Text, out var p) || p is < 1 or > 65535)
            { MessageBox.Show("Укажіть коректний порт.", "Нове підключення"); return; }
            var targetRoom = roomChoice?.SelectedValue as string;
            if (targetRoom == null)
            {
                Edit(new { op = "addRoom", name = "Швидкі підключення" });
                targetRoom = state.Rooms.LastOrDefault()?.Id;
            }
            if (targetRoom == null) return;
            var oldComputers = state.Computers.Select(c => c.Id).ToHashSet();
            var position = FreePosition(targetRoom, 128, 82);
            Edit(new { op = "addComputer", roomId = targetRoom, x = position.X, y = position.Y, name = deviceName.Text.Trim() });
            var newComputer = state.Computers.FirstOrDefault(c => !oldComputers.Contains(c.Id));
            if (newComputer == null) return;
            var oldEndpoints = state.Endpoints.Select(e => e.Id).ToHashSet();
            Edit(new { op = "addEndpoint", computerId = newComputer.Id, protocol = (string)protocol.SelectedItem,
                host = host.Text.Trim(), port = p, username = user.Text.Trim() });
            created = state.Endpoints.FirstOrDefault(e => !oldEndpoints.Contains(e.Id));
            if (created == null) return;
            if (password.Password.Length > 0) Credentials.Save(created.Id, user.Text.Trim(), password.Password);
            roomId = targetRoom; computerId = newComputer.Id; deskId = null; inspectorOpen = true; UpdateInspector(); Refresh();
            dialog.DialogResult = true; dialog.Close();
        }, true));
        body.Children.Add(actions);
        dialog.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (dialog.ShowDialog() == true && created != null) OpenSession(created);
    }
    private void PasswordDialog(Endpoint ep)
    {
        var w = new Window { Title = "Пароль · " + ep.Protocol, Width = 350, Height = 190, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = panel, ResizeMode = ResizeMode.NoResize };
        var stack = new StackPanel { Margin = new Thickness(22) };
        stack.Children.Add(Label("Новий пароль для " + ep.Host, 13, true));
        var input = new PasswordBox { Margin = new Thickness(0, 14, 0, 18), Padding = Pad(9, 7) }; stack.Children.Add(input);
        stack.Children.Add(Btn("Зберегти в Windows", (_, _) => { Credentials.Save(ep.Id, ep.Username, input.Password); w.DialogResult = true; w.Close(); }, true));
        w.Content = stack; w.ShowDialog();
    }
    private string? Prompt(string title, string label, string initial)
    {
        var w = new Window { Title = title, Width = 370, Height = 180, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = panel, ResizeMode = ResizeMode.NoResize };
        var stack = new StackPanel { Margin = new Thickness(22) };
        stack.Children.Add(Label(label, 13, true)); var input = Box(initial); stack.Children.Add(input);
        stack.Children.Add(Btn("Створити", (_, _) => { if (input.Text.Trim().Length > 0) { w.DialogResult = true; w.Close(); } }, true));
        w.Content = stack; return w.ShowDialog() == true ? input.Text.Trim() : null;
    }
    private void Export()
    {
        var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "RemoteRooms-address-book.json" };
        if (dialog.ShowDialog(this) != true) return;
        using var doc = JsonDocument.Parse(CoreBridge.StateJson());
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }));
        MessageBox.Show("Адресну книгу експортовано без паролів.", "Експорт");
    }
    private void OpenSession(Endpoint ep)
    {
        var view = new SessionView(ep, folder, Credentials.Load(ep.Id));
        view.ApplyTheme(light);
        ShowSessionWindow(view, true);
    }
    private void ShowSessionWindow(SessionView view, bool fullscreen)
    {
        var window = new SessionHostWindow(this, view, fullscreen, AttachTab);
        hostedSessions.Add(window);
        window.Closed += (_, _) => hostedSessions.Remove(window);
        try { window.Show(); }
        catch
        {
            hostedSessions.Remove(window);
            view.Dispose();
            throw;
        }
    }
    private void AttachTab(SessionView view)
    {
        if (!IsLoaded) { view.Dispose(); return; }
        var ep = view.Endpoint;
        view.ApplyTheme(light);
        view.SetImmersive(false);
        var tab = new TabItem { Content = view };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(Label($"●  {ep.Protocol} · {ep.Host}", 12, true));
        var closeButton = new Button { Content = "×", Width = 22, Height = 22, Margin = new Thickness(10, 0, 0, 0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = muted, Cursor = Cursors.Hand,
            ToolTip = "Закрити сеанс" };
        closeButton.Click += (_, _) => CloseTab(tab);
        header.Children.Add(closeButton);
        tab.Header = header;
        var menu = new ContextMenu();
        var fullscreen = new MenuItem { Header = "На весь екран" };
        fullscreen.Click += (_, _) => FullscreenTab(tab); menu.Items.Add(fullscreen);
        var detach = new MenuItem { Header = "Винести в окреме вікно" };
        detach.Click += (_, _) => Detach(tab); menu.Items.Add(detach);
        var close = new MenuItem { Header = "Закрити сеанс" };
        close.Click += (_, _) => CloseTab(tab); menu.Items.Add(close);
        tab.ContextMenu = menu;
        view.ConfigureWindowActions("На весь екран", () => FullscreenTab(tab));
        sessions[tab] = view; tabs.Items.Add(tab); tabs.SelectedItem = tab;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
    private void CloseTab(TabItem tab)
    {
        if (!sessions.Remove(tab, out var view)) return;
        tabs.Items.Remove(tab); view.Dispose();
    }
    private void Detach(TabItem tab)
    {
        if (!sessions.Remove(tab, out var view)) return;
        tab.Content = null; tabs.Items.Remove(tab);
        ShowSessionWindow(view, false);
    }
    private void FullscreenTab(TabItem tab)
    {
        if (!sessions.Remove(tab, out var view)) return;
        tab.Content = null; tabs.Items.Remove(tab);
        ShowSessionWindow(view, true);
    }
}

