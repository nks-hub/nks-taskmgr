using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices;

namespace NksTaskMgr.WinUI;

public sealed partial class ProcessIconView : Grid
{
    private readonly Image image = new() { Width = 18, Height = 18 };
    private readonly FrameworkElement fallback = UiIcon.Create(false, 0x95a4ad, 18);
    private CancellationTokenSource? loading;
    private ProcessRow? row;
    public ProcessRow? Row {
        get => row;
        set { if (ReferenceEquals(row, value)) return; row = value; if (IsLoaded) Load(); }
    }

    public ProcessIconView()
    {
        Children.Add(fallback); Children.Add(image);
        Loaded += (_, _) => Load();
        Unloaded += (_, _) => Cancel();
    }

    private void Cancel()
    {
        loading?.Cancel(); loading?.Dispose(); loading = null;
    }

    private async void Load()
    {
        Cancel(); image.Source = null; fallback.Visibility = Visibility.Visible;
        if (row is null) return;
        var key = row.Key;
        loading = new CancellationTokenSource(); var cancellation = loading.Token;
        try {
            var bytes = await ProcessIcons.Load(key, cancellation);
            if (bytes is null || cancellation.IsCancellationRequested || !IsLoaded || row?.Key != key) return;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream)) { writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); }
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = 18, DecodePixelHeight = 18 };
            await bitmap.SetSourceAsync(stream);
            if (cancellation.IsCancellationRequested || !IsLoaded || row?.Key != key) return;
            image.Source = bitmap; fallback.Visibility = Visibility.Collapsed;
        } catch (COMException) {
            if (cancellation.IsCancellationRequested || !IsLoaded || row?.Key != key) return;
            image.Source = null; fallback.Visibility = Visibility.Visible;
        }
    }
}
