using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal static partial class ProcessIcons
{
    private const int Size = 18;
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
        if (key.Kind != GenerationKind.CreationTime || key.Generation == 0) return null;
        using var process = OpenProcess(0x1000, false, key.Pid);
        if (process.IsInvalid || !GetProcessTimes(process, out ulong created, out _, out _, out _) || created != key.Generation) return null;
        char[] buffer = new char[32768]; uint length = (uint)buffer.Length;
        fixed (char* pathBuffer = buffer) if (!QueryFullProcessImageName(process, 0, pathBuffer, ref length)) return null;
        string path = new(buffer, 0, (int)length);
        if (path.Length < 3 || path[1] != ':' || GetDriveType(path[..3]) != 3) return null;
        lock (cache) if (cache.TryGetValue(path, out var cached)) return cached;
        byte[]? bytes;
        int initialized = CoInitializeEx(0, 0);
        try {
            if (initialized < 0 && initialized != unchecked((int)0x80010106)) return null;
            if (SHGetFileInfo(path, 0, out var info, (uint)sizeof(ShellFileInfo), 0x101) == 0 || info.Icon == 0) return null;
            try { bytes = Bitmap(info.Icon); } finally { DestroyIcon(info.Icon); }
        } finally { if (initialized >= 0) CoUninitialize(); }
        if (bytes is null) return null;
        lock (cache) {
            if (!cache.ContainsKey(path)) {
                if (cache.Count == 128) cache.Remove(cachedPaths.Dequeue());
                cache.Add(path, bytes); cachedPaths.Enqueue(path);
            }
        }
        return bytes;
    }

    private static unsafe byte[]? Bitmap(nint icon)
    {
        nint dc = CreateCompatibleDC(0);
        if (dc == 0) return null;
        var header = new BitmapHeader { Length = 40, Width = Size, Height = -Size, Planes = 1, Bits = 32 };
        nint bitmap = CreateDIBSection(dc, ref header, 0, out var pixels, 0, 0);
        nint previous = 0;
        try {
            if (bitmap == 0 || pixels == 0) return null;
            previous = SelectObject(dc, bitmap);
            if (previous == 0 || previous == -1) return null;
            var target = new Span<uint>((void*)pixels, Size * Size); target.Fill(0xff1d252a);
            if (!DrawIconEx(dc, 0, 0, icon, Size, Size, 0, 0, 3)) return null;
            GdiFlush();
            for (int i = 0; i < target.Length; ++i) target[i] |= 0xff000000;
            var bytes = new byte[54 + Size * Size * 4];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, 0x4d42);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), Size);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), -Size);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 32);
            Marshal.Copy(pixels, bytes, 54, Size * Size * 4);
            return bytes;
        } finally {
            if (previous != 0 && previous != -1) SelectObject(dc, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public uint Length;
        public int Width, Height;
        public ushort Planes, Bits;
        public uint Compression, ImageSize;
        public int XPixels, YPixels;
        public uint Colors, Important;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct ShellFileInfo
    {
        public nint Icon;
        public int Index;
        public uint Attributes;
        public fixed char DisplayName[260];
        public fixed char TypeName[80];
    }
    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess")]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint mode);
    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();
    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessTimes")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out ulong created, out ulong ended, out ulong kernel, out ulong user);
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* path, ref uint length);
    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveType(string root);
    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);
    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint dc, ref BitmapHeader header, uint usage, out nint pixels, nint section, uint offset);
    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint bitmap);
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint value);
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GdiFlush();
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
}
