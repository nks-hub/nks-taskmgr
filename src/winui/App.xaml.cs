using Microsoft.UI.Xaml;

namespace NksTaskMgr.WinUI;

public partial class App : Application
{
    internal static string? SmokePath { get; } = Environment.GetCommandLineArgs()
        .FirstOrDefault(arg => arg.StartsWith("--smoke=", StringComparison.Ordinal))?[8..];
    internal static void Trace(string stage) { if (SmokePath is not null) File.AppendAllText(SmokePath, stage + Environment.NewLine); }
    private Window? window;
    public App()
    {
        UnhandledException += (_, args) => {
            Trace(args.Exception.ToString());
        };
        if (!Environment.GetCommandLineArgs().Contains("--render-check")) InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Trace("Constructing window");
        if (Environment.GetCommandLineArgs().Contains("--render-check")) {
            var content = new Microsoft.UI.Xaml.Controls.Grid { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red) };
            content.Loaded += async (_, _) => {
                var color = ((Microsoft.UI.Xaml.Media.SolidColorBrush)content.Background).Color;
                Trace($"Loaded={content.IsLoaded};host={content.XamlRoot.IsHostVisible};size={content.ActualWidth}x{content.ActualHeight};color={color.A},{color.R},{color.G},{color.B}");
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                await bitmap.RenderAsync(content);
                var buffer = await bitmap.GetPixelsAsync();
                var pixels = new byte[buffer.Length];
                using var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer); reader.ReadBytes(pixels);
                int center = ((bitmap.PixelHeight / 2) * bitmap.PixelWidth + bitmap.PixelWidth / 2) * 4;
                Trace($"Bitmap={bitmap.PixelWidth}x{bitmap.PixelHeight};center={pixels[center]},{pixels[center + 1]},{pixels[center + 2]},{pixels[center + 3]}");
            };
            window = new Window { Content = content }; window.Activate(); return;
        }
        window = new MainWindow();
        window.Activate();
    }
}
