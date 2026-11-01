using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NksTaskMgr.WinUI;

internal sealed class ProcessDumpQaWindow : Window
{
    private readonly CancellationTokenSource cancellation = new(TimeSpan.FromMinutes(3));
    private readonly object logLock = new();
    private readonly string path;
    private readonly TextBlock status = new() { Text = "Ověřuji dumpy pouze vlastních testovacích procesů…",
        TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 13, Margin = new Thickness(20) };
    private bool started, allowClose;

    internal ProcessDumpQaWindow(string path)
    {
        this.path = Path.GetFullPath(path);
        Title = "NKS Task Manager · ověření dumpů";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 400));
        string icon = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        Content = status;
        status.Loaded += async (_, _) => { if (!started) { started = true; await RunAsync(); } };
        AppWindow.Closing += (_, args) => {
            if (allowClose) return;
            args.Cancel = true; cancellation.Cancel();
            status.Text = "Ruším čekání a dokončuji úklid vlastních dumpů a testovacích procesů…";
        };
    }

    private void Log(string message)
    {
        lock (logLock) File.AppendAllText(path, message + Environment.NewLine);
    }

    private async Task RunAsync()
    {
        bool passed = false;
        try {
            string executable = Environment.ProcessPath ?? throw new IOException("Actual GUI image is unavailable.");
            var artifacts = new[] { executable, Path.Combine(AppContext.BaseDirectory, "nks-dump-worker.exe"),
                Path.Combine(AppContext.BaseDirectory, "nks-dump-worker-x86.exe") };
            var before = artifacts.Select(FileHash).ToArray();
            Log($"dump-ui begin;guiPid={Environment.ProcessId};version={FileVersionInfo.GetVersionInfo(executable).ProductVersion};guiSha256={before[0]}");
            string directory = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-artifacts");
            if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Dump QA artifacts directory already exists.");
            Directory.CreateDirectory(directory);
            passed = await ProcessDumpQaChecks.RunAsync(directory, Log, cancellation.Token);
            if (!before.SequenceEqual(artifacts.Select(FileHash))) throw new IOException("GUI/dump helper changed during QA.");
        } catch (Exception error) { passed = false; Log("FAIL dump-ui " + error); }
        finally {
            passed &= !cancellation.IsCancellationRequested;
            Log($"{(passed ? "PASS" : "FAIL")} dump-ui finished;passed={passed};cancelled={cancellation.IsCancellationRequested}");
            Environment.ExitCode = passed ? 0 : 1;
            allowClose = true; cancellation.Dispose(); Close(); Application.Current.Exit();
        }
    }

    private static string FileHash(string file)
    {
        using var input = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(input));
    }
}
