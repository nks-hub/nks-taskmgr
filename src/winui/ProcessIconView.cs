using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace NksTaskMgr.WinUI;

public sealed partial class ProcessIconView : Grid
{
    private static readonly Dictionary<ProcessKey, BitmapImage> decoded = [];
    private static readonly Queue<ProcessKey> decodedOrder = [];
    private static readonly ConditionalWeakTable<byte[], Task<BitmapImage>> sharedBitmaps = new();
    internal static int DecodedImagesCreated { get; private set; }
    internal static bool SuppressForBenchmark { get; set; }

    internal static Task<BitmapImage> DecodeShared(byte[] bytes)
    {
        if (sharedBitmaps.TryGetValue(bytes, out var previous) && (previous.IsFaulted || previous.IsCanceled)) sharedBitmaps.Remove(bytes);
        return sharedBitmaps.GetValue(bytes, DecodeBitmap);
    }

    private static async Task<BitmapImage> DecodeBitmap(byte[] bytes)
    {
        ++DecodedImagesCreated;
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream)) { writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); }
        stream.Seek(0);
        var bitmap = new BitmapImage { DecodePixelWidth = 18, DecodePixelHeight = 18 };
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }
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
        Cancel();
        if (row is null || SuppressForBenchmark) { image.Source = null; fallback.Visibility = Visibility.Visible; return; }
        var key = row.Key;
        if (decoded.TryGetValue(key, out var cached)) { image.Source = cached; fallback.Visibility = Visibility.Collapsed; return; }
        image.Source = null; fallback.Visibility = Visibility.Visible;
        loading = new CancellationTokenSource(); var cancellation = loading.Token;
        try {
            var bytes = await ProcessIcons.Load(key, cancellation);
            if (bytes is null || cancellation.IsCancellationRequested || !IsLoaded || row?.Key != key) return;
            var bitmap = await DecodeShared(bytes);
            if (cancellation.IsCancellationRequested || !IsLoaded || row?.Key != key) return;
            if (!decoded.ContainsKey(key)) {
                if (decoded.Count == 128) decoded.Remove(decodedOrder.Dequeue());
                decoded.Add(key, bitmap); decodedOrder.Enqueue(key);
            }
            image.Source = bitmap; fallback.Visibility = Visibility.Collapsed;
        } catch (COMException) {
            if (cancellation.IsCancellationRequested || !IsLoaded || row?.Key != key) return;
            image.Source = null; fallback.Visibility = Visibility.Visible;
        }
    }
}
