using System.Windows;
using System.Windows.Media.Imaging;

namespace RemoteRooms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message, "Помилка RemoteRooms", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        var folder = Environment.GetEnvironmentVariable("REMOTEROOMS_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteRooms");
        Directory.CreateDirectory(folder);
        CoreBridge.Open(Path.Combine(folder, "address-book.json"));
        var window = new MainWindow(folder);
        if (args.Length == 2 && args[0] == "--smoke")
        {
            File.WriteAllText(args[1], "OK");
            window.Close();
            return;
        }
        if ((args.Length == 2 || args.Length == 4) && args[0] == "--screenshot")
        {
            int width = 1040, height = 720;
            if (args.Length == 4 && (!int.TryParse(args[2], out width) || !int.TryParse(args[3], out height)
                || width < 640 || height < 480)) throw new ArgumentException("Некоректний розмір знімка.");
            window.SetPreviewSize(width, height);
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();
            var image = new RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            image.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(args[1]);
            encoder.Save(stream);
            window.Close();
            return;
        }
        app.Run(window);
    }
}
