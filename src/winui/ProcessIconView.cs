using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace NksTaskMgr.WinUI;

public sealed partial class ProcessIconView : Grid
{
    private static readonly Dictionary<ProcessKey, BitmapImage> decoded = [];
    private static readonly Queue<ProcessKey> decodedOrder = [];
    private static readonly ConditionalWeakTable<byte[], Task<BitmapImage>> sharedBitmaps = new();
    internal static int DecodedImagesCreated { get; private set; }
    internal static bool SuppressForBenchmark { get; set; }
    internal static double ApplyMs { get; private set; }
    internal static double DecodeStartMs { get; private set; }
    internal static double ApplyMaxMs { get; private set; }
    internal static double DecodeStartMaxMs { get; private set; }
    internal static void BeginProfileSample() { ApplyMaxMs = DecodeStartMaxMs = 0; }

    internal static Task<BitmapImage> DecodeShared(byte[] bytes)
    {
        long began = App.SortBenchmarkPath is null ? 0 : Stopwatch.GetTimestamp();
        if (sharedBitmaps.TryGetValue(bytes, out var previous) && (previous.IsFaulted || previous.IsCanceled)) sharedBitmaps.Remove(bytes);
        var task = sharedBitmaps.GetValue(bytes, DecodeBitmap);
        if (began != 0) { double elapsed = Stopwatch.GetElapsedTime(began).TotalMilliseconds; DecodeStartMs += elapsed; DecodeStartMaxMs = Math.Max(DecodeStartMaxMs, elapsed); }
        return task;
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

    private void ApplyImage(BitmapImage? bitmap)
    {
        long began = App.SortBenchmarkPath is null ? 0 : Stopwatch.GetTimestamp();
        image.Source = bitmap; fallback.Visibility = bitmap is null ? Visibility.Visible : Visibility.Collapsed;
        if (began != 0) { double elapsed = Stopwatch.GetElapsedTime(began).TotalMilliseconds; ApplyMs += elapsed; ApplyMaxMs = Math.Max(ApplyMaxMs, elapsed); }
    }

    private async void Load()
    {
        Cancel();
        if (row is null || SuppressForBenchmark) { ApplyImage(null); return; }
        var key = row.Key;
        if (decoded.TryGetValue(key, out var cached)) { ApplyImage(cached); return; }
        ApplyImage(null);
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
            ApplyImage(bitmap);
        } catch (COMException) {
            if (cancellation.IsCancellationRequested || !IsLoaded || row?.Key != key) return;
            ApplyImage(null);
        }
    }
}
