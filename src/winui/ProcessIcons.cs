using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal static partial class ProcessIcons
{
    private static readonly SemaphoreSlim workers = new(2);
    private static readonly Dictionary<string, byte[]> cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> cachedPaths = new();
    private static int pending;

    public static async Task<byte[]?> Load(ProcessKey key, CancellationToken cancellation)
    {
        if (Interlocked.Increment(ref pending) > 64) { Interlocked.Decrement(ref pending); return null; }
        bool entered = false;
        try {
            await workers.WaitAsync(cancellation).ConfigureAwait(false); entered = true;
            return await Task.Run(() => Read(key), cancellation).ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return null; }
        finally { if (entered) workers.Release(); Interlocked.Decrement(ref pending); }
    }

    internal static unsafe byte[]? Read(ProcessKey key)
    {
        if (key.Kind is not (GenerationKind.CreationTime or GenerationKind.SequenceNumber) || key.Generation == 0 || key.Boot.IsEmpty) return null;
        using var process = OpenProcess(0x1000, false, key.Pid);
        if (process.IsInvalid) return null;
        var identity = NativeMonitor.QueryProcessIdentity(process, key.Kind);
        if (identity.State != SampleState.Valid || identity.Boot != key.Boot || identity.Pid != key.Pid
            || identity.Kind != key.Kind || identity.Generation != key.Generation) return null;
        char[] buffer = new char[32768]; uint length = (uint)buffer.Length;
        fixed (char* pathBuffer = buffer) if (!QueryFullProcessImageName(process, 0, pathBuffer, ref length)) return null;
        string path = new(buffer, 0, (int)length);
        if (path.Length < 3 || path[1] != ':') return null;
        lock (cache) if (cache.TryGetValue(path, out var cached)) return cached;
        byte[]? bytes = IconWorkers.Read(key);
        if (bytes is null) return null;
        lock (cache) {
            if (cache.TryGetValue(path, out var existing)) return existing;
            if (cache.Count == 128) cache.Remove(cachedPaths.Dequeue());
            cache.Add(path, bytes); cachedPaths.Enqueue(path);
        }
        return bytes;
    }

    /// <summary>Image path of the exact process generation, or null when it ended, was reused or is not readable.</summary>
    internal static unsafe string? ImagePath(ProcessKey key)
    {
        if (key.Kind is not (GenerationKind.CreationTime or GenerationKind.SequenceNumber) || key.Generation == 0 || key.Boot.IsEmpty) return null;
        using var process = OpenProcess(0x1000, false, key.Pid);
        if (process.IsInvalid) return null;
        var identity = NativeMonitor.QueryProcessIdentity(process, key.Kind);
        if (identity.State != SampleState.Valid || identity.Boot != key.Boot || identity.Pid != key.Pid
            || identity.Kind != key.Kind || identity.Generation != key.Generation) return null;
        char[] buffer = new char[32768]; uint length = (uint)buffer.Length;
        fixed (char* pathBuffer = buffer) if (!QueryFullProcessImageName(process, 0, pathBuffer, ref length)) return null;
        return new string(buffer, 0, (int)length);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess")]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* path, ref uint length);
}
