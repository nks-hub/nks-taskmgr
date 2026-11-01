using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32.SafeHandles;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal sealed partial class ProcessActionsUiChecks : Window
{
    private const uint BelowNormal = 0x4000;
    private const string Ready = "NKS_ACTION_READY", Resumed = "NKS_ACTION_RESUMED";
    private const string ChildScript = "$ErrorActionPreference='Stop';[Console]::WriteLine('NKS_ACTION_READY');"
        + "if([Console]::ReadLine() -ne 'continue'){exit 17};[Console]::WriteLine('NKS_ACTION_RESUMED');"
        + "if([Console]::ReadLine() -ne 'finish'){exit 18};exit 42";
    private readonly TextBlock status = new() { Text = "Ověřuji akce pouze na vlastních testovacích procesech…", TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true, Margin = new Thickness(20), FontSize = 13 };
    private readonly CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(90));
    private readonly ProcessActionsClient client = new(elevated: false);
    private readonly List<OwnedChild> children = [];
    private readonly string path;
    private SafeFileHandle? childJob;
    private bool allowClose, started, running = true, cpuSetsCovered;
    private int checks;

    internal ProcessActionsUiChecks(string path)
    {
        this.path = Path.GetFullPath(path); Title = "NKS Task Manager · ověření procesových akcí";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 400));
        string icon = Path.Combine(AppContext.BaseDirectory, "app.ico"); if (File.Exists(icon)) AppWindow.SetIcon(icon);
        Content = new ScrollViewer { Content = status };
        status.Loaded += async (_, _) => { if (!started) { started = true; await RunAsync(); } };
        AppWindow.Closing += (_, args) => {
            if (allowClose) return;
            args.Cancel = true; cancellation.Cancel(); status.Text = "Ruším čekání a dokončuji úklid vlastních procesů…";
        };
        Closed += (_, _) => { if (running) cancellation.Cancel(); };
    }
    private void Log(string text) => File.AppendAllText(path, text + Environment.NewLine);
    private void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        ++checks;
    }
    private sealed class OwnedChild(Process process)
    {
        internal readonly Process Process = process;
        internal SafeProcessHandle Handle => Process.SafeHandle;
        internal ProcessActionTarget Target = null!;
        internal bool Assigned;
        internal Task<string?>? PendingLine;
        internal readonly Task<string> Error = ReadErrorAsync(process.StandardError);
        internal Task<string?> ReadLine()
        {
            var read = Process.StandardOutput.ReadLineAsync(); PendingLine = read;
            _ = read.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return read;
        }
        private static async Task<string> ReadErrorAsync(StreamReader reader)
        {
            var text = new StringBuilder(); var buffer = new char[512];
            while (text.Length < 8192) {
                int count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (count == 0) return text.ToString();
                text.Append(buffer, 0, count);
            }
            return text + " [truncated]";
        }
    }
    private async Task<OwnedChild> StartChildAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(ChildScript)) }) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Own gated child did not start.");
        var child = new OwnedChild(process); children.Add(child);
        Require(process.Id != Environment.ProcessId, "The fixture selected the GUI itself.");
        if (!AssignProcessToJobObject(childJob!, child.Handle)) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Assign own gated child to abort job");
        child.Assigned = true;
        Require(GetTimes(child.Handle, out ulong created, out _, out _, out _), "Own child creation FILETIME is unavailable.");
        var identity = NativeMonitor.QueryProcessIdentity(child.Handle, GenerationKind.CreationTime);
        Require(identity.State == SampleState.Valid && identity.Kind == GenerationKind.CreationTime && identity.Generation == created
            && identity.Pid == (uint)process.Id && (identity.BootLow | identity.BootHigh) != 0 && created != 0,
            "Own held child identity disagrees with the independent FILETIME oracle.");
        child.Target = new(new(new(identity.BootLow, identity.BootHigh), identity.Pid, identity.Kind, identity.Generation), created);
        Require(await child.ReadLine().WaitAsync(TimeSpan.FromSeconds(5), token) == Ready, "Own gated child did not reach its ready marker within five seconds.");
        Log($"child ready;pid={process.Id};key={child.Target.Key};rawCT={created}");
        return child;
    }
    private bool Alive(OwnedChild child)
    {
        uint wait = WaitForSingleObject(child.Handle, 0);
        if (wait == 0) return false;
        Require(wait == 258, "Original child handle could not prove liveness."); return true;
    }
    private void CheckOriginal(OwnedChild child, bool alive = true)
    {
        Require(GetTimes(child.Handle, out ulong created, out _, out _, out _) && created == child.Target.Created100ns,
            "Original child handle changed its creation identity.");
        if (alive) Require(Alive(child), "Own gated child ended before the next action.");
    }
    private async Task<ProcessActionsBatch> SendAsync(OwnedChild child, ProcessActionsRequest request, CancellationToken token, bool alive = true)
    {
        CheckOriginal(child, alive); token.ThrowIfCancellationRequested();
        Log($"request command={request.Command};target={request.Targets.Single().Key};rawCT={request.Targets.Single().Created100ns};argument={request.Argument};group={request.Group}");
        ProcessActionsBatch batch;
        try { batch = await client.ExecuteAsync(request, token); }
        catch (Exception error) {
            Log($"FAIL transport;command={request.Command};outcomeUnknown={request.Command is not (ProcessActionCommand.Query or ProcessActionCommand.QueryCpuSets)};noRetry=True;error={error}");
            throw;
        }
        Require(batch.Accepted && batch.Error == 0 && batch.Frame is ProcessActionFrame.Results or ProcessActionFrame.CpuSets && batch.Results.Length == 1,
            "The real helper did not deliver an accepted complete result and successful exit/EOF.");
        var row = batch.Results.Single();
        Require(row.Target == request.Targets.Single(), "The helper result belongs to a different full target.");
        Log($"result command={request.Command};outcome={row.Outcome};reason={row.Reason};flags={row.Flags};win32={row.Error};ntstatus=0x{row.NtStatus:x8};observedCT={row.ObservedCreated100ns};priority={row.PriorityClass};mask={row.ProcessMask};systemMask={row.SystemMask};group={row.Group}");
        return batch;
    }
    private static ProcessActionsRequest Request(OwnedChild child, ProcessActionCommand command, ulong argument = 0, uint group = 0,
        ProcessCpuSetsSelection? cpuSets = null) => new(command, [child.Target], argument, group, cpuSets);
    private void Applied(ProcessActionResult row)
        => Require(row.Outcome == ProcessActionOutcome.Applied && row.Error == 0 && row.Flags.HasFlag(ProcessActionFlags.EffectConfirmed)
            && row.Flags.HasFlag(ProcessActionFlags.IdentityVerified), "The helper did not confirm the applied mutation.");
    private uint Priority(OwnedChild child)
    {
        uint value = GetPriorityClass(child.Handle); Require(value != 0, "Independent priority oracle is unavailable."); return value;
    }
    private (ulong Process, ulong System) Affinity(OwnedChild child)
    {
        Require(GetProcessAffinityMask(child.Handle, out nuint process, out nuint system) && process != 0 && system != 0,
            "Independent affinity oracle is unavailable."); return ((ulong)process, (ulong)system);
    }
    private async Task CheckSettingsAsync(OwnedChild child, CancellationToken token)
    {
        var query = (await SendAsync(child, Request(child, ProcessActionCommand.Query), token)).Results.Single();
        Require(query.Outcome == ProcessActionOutcome.Inspected && query.Flags.HasFlag(ProcessActionFlags.PriorityAvailable)
            && query.Flags.HasFlag(ProcessActionFlags.AffinityAvailable) && query.Flags.HasFlag(ProcessActionFlags.AliveAfter),
            "A live own child did not expose a verified priority and single-group affinity.");
        uint originalPriority = Priority(child); var originalAffinity = Affinity(child);
        Require(query.PriorityClass == originalPriority && query.ProcessMask == originalAffinity.Process && query.SystemMask == originalAffinity.System,
            "Query disagrees with independent held-handle metadata.");
        ulong differentPriority = originalPriority == BelowNormal ? 0x20u : BelowNormal;
        foreach (bool rawMismatch in new[] { false, true }) {
            var bad = rawMismatch ? child.Target with { Created100ns = child.Target.Created100ns + 1 }
                : child.Target with { Key = child.Target.Key with { Generation = child.Target.Key.Generation + 1 } };
            var refused = (await SendAsync(child, new(ProcessActionCommand.Priority, [bad], differentPriority), token)).Results.Single();
            Require(refused.Outcome == ProcessActionOutcome.IdentityChanged && !refused.Flags.HasFlag(ProcessActionFlags.MayHaveApplied)
                && refused.Reason == (rawMismatch ? ProcessActionReason.RawTimeMismatch : ProcessActionReason.IdentityUnverifiable),
                "A wrong full generation/raw FILETIME was not refused before mutation.");
            Require(Priority(child) == originalPriority && Affinity(child).Process == originalAffinity.Process,
                "A refused lifetime changed the real child settings.");
        }
        Applied((await SendAsync(child, Request(child, ProcessActionCommand.Priority, BelowNormal), token)).Results.Single());
        Require(Priority(child) == BelowNormal, "Independent readback did not observe BelowNormal.");
        Applied((await SendAsync(child, Request(child, ProcessActionCommand.Priority, originalPriority), token)).Results.Single());
        Require(Priority(child) == originalPriority, "Independent priority restore failed.");
        ulong one = originalAffinity.Process & unchecked(0UL - originalAffinity.Process);
        Applied((await SendAsync(child, Request(child, ProcessActionCommand.Affinity, one, query.Group), token)).Results.Single());
        Require(Affinity(child).Process == one, "Independent affinity readback did not observe the chosen bit.");
        Applied((await SendAsync(child, Request(child, ProcessActionCommand.Affinity, originalAffinity.Process, query.Group), token)).Results.Single());
        Require(Affinity(child) == originalAffinity, "Independent affinity restore failed.");
        await CheckCpuSetsAsync(child, query.Group, originalAffinity.Process, token);
    }
    private async Task CheckCpuSetsAsync(OwnedChild child, uint group, ulong mask, CancellationToken token)
    {
        var query = await SendAsync(child, Request(child, ProcessActionCommand.QueryCpuSets), token);
        var row = query.Results.Single(); var before = query.CpuSets;
        if (row.Outcome == ProcessActionOutcome.Unsupported && before is { Valid: false }) {
            Log($"NOT_COVERED CPU sets;reason={row.Reason};win32={row.Error};noFakeSuccess=True"); return;
        }
        Require(row.Outcome == ProcessActionOutcome.Inspected && before is { Valid: true }, "CPU sets query failed without a definitive unsupported state.");
        var snapshot = before!; uint[] original = CpuSetOracle(child.Handle);
        Require(original.SequenceEqual(snapshot.DefaultIds), "CPU sets query disagrees with the independent oracle.");
        var chosen = snapshot.Processors.FirstOrDefault(cpu => cpu.Available && cpu.Group == group && cpu.LogicalProcessor < 64
            && (mask & (1UL << (int)cpu.LogicalProcessor)) != 0);
        Require(chosen is not null, "No available CPU set intersects the verified group/affinity.");
        uint[] requested = original.SequenceEqual(new[] { chosen!.Id }) ? [] : [chosen.Id];
        var changed = await SendAsync(child, Request(child, ProcessActionCommand.CpuSets,
            cpuSets: new(snapshot.Baseline, requested)), token);
        Applied(changed.Results.Single());
        Require(CpuSetOracle(child.Handle).SequenceEqual(requested), "Independent CPU sets readback differs from the requested selection.");
        var refresh = await SendAsync(child, Request(child, ProcessActionCommand.QueryCpuSets), token);
        Require(refresh.Results.Single().Outcome == ProcessActionOutcome.Inspected && refresh.CpuSets is { Valid: true }, "Fresh CPU sets baseline is unavailable before restore.");
        var restored = await SendAsync(child, Request(child, ProcessActionCommand.CpuSets,
            cpuSets: new(refresh.CpuSets!.Baseline, original)), token);
        Applied(restored.Results.Single());
        Require(CpuSetOracle(child.Handle).SequenceEqual(original), "Independent CPU sets restore failed."); cpuSetsCovered = true;
        Log("CPU sets query/set/fresh-baseline/restore confirmed by original held handle.");
    }
    private static unsafe uint[] CpuSetOracle(SafeProcessHandle handle)
    {
        uint* values = stackalloc uint[4096];
        if (!GetDefaultCpuSets(handle, values, 4096, out uint count)) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Independent CPU sets oracle");
        if (count > 4096) throw new InvalidDataException("Independent CPU sets count exceeds the bound.");
        var result = new ReadOnlySpan<uint>(values, (int)count).ToArray(); Array.Sort(result);
        if (result.Distinct().Count() != result.Length) throw new InvalidDataException("Independent CPU sets oracle returned duplicate IDs.");
        return result;
    }
    private async Task CheckSuspendResumeAsync(OwnedChild child, CancellationToken token)
    {
        var suspended = (await SendAsync(child, Request(child, ProcessActionCommand.Suspend), token)).Results.Single();
        Require(suspended.Outcome == ProcessActionOutcome.NativeRequestAccepted && suspended.NtStatus == 0
            && suspended.Flags.HasFlag(ProcessActionFlags.AliveAfter), "Suspend did not return a live native acceptance.");
        Task<string?> gate = child.ReadLine();
        await child.Process.StandardInput.WriteLineAsync("continue".AsMemory(), token);
        await child.Process.StandardInput.FlushAsync(token);
        Require(await Task.WhenAny(gate, Task.Delay(250, token)) != gate, "The gated child advanced while suspended.");
        token.ThrowIfCancellationRequested(); CheckOriginal(child);
        var resumed = (await SendAsync(child, Request(child, ProcessActionCommand.Resume), token)).Results.Single();
        Require(resumed.Outcome == ProcessActionOutcome.NativeRequestAccepted && resumed.NtStatus == 0
            && resumed.Flags.HasFlag(ProcessActionFlags.AliveAfter), "Resume did not return a live native acceptance.");
        Require(await gate.WaitAsync(TimeSpan.FromSeconds(5), token) == Resumed, "The same pending gate read did not advance after resume.");
        CheckOriginal(child); Log("Suspend/gate/resume confirmed; no claim about a particular suspend count.");
    }
    private async Task WaitExitedAsync(OwnedChild child, CancellationToken token)
    {
        long began = Stopwatch.GetTimestamp();
        while (Alive(child)) {
            token.ThrowIfCancellationRequested();
            Require(Stopwatch.GetElapsedTime(began) < TimeSpan.FromSeconds(5), "Owned child did not signal exit within five seconds.");
            await Task.Delay(25, token);
        }
    }
    private async Task RunAsync()
    {
        bool bodyPassed = false;
        try {
            CheckOrdinaryToken();
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Actual GUI image is unavailable.");
            string helper = Path.Combine(AppContext.BaseDirectory, "nks-actions-worker.exe");
            string guiHash = FileHash(executable), helperHash = FileHash(helper);
            Log($"actions-check begin;guiPid={Environment.ProcessId};gui={executable};version={FileVersionInfo.GetVersionInfo(executable).ProductVersion};guiSha256={guiHash};helperSha256={helperHash};brokerPinConfigured={PublisherTrust.IsConfigured(PublisherPolicy.Broker)};elevatedClient=False");
            childJob = CreateJobObject(0, 0);
            if (childJob.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Create owned child abort job");
            var limits = new JobLimits { Basic = new BasicLimits { Flags = 0x2000 } };
            if (!SetInformationJobObject(childJob, 9, in limits, (uint)Marshal.SizeOf<JobLimits>())) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Set owned child abort job");
            var token = cancellation.Token;
            var child = await StartChildAsync(token); await CheckSettingsAsync(child, token); await CheckSuspendResumeAsync(child, token);
            var terminated = (await SendAsync(child, Request(child, ProcessActionCommand.Terminate), token)).Results.Single();
            Require(terminated.Outcome is ProcessActionOutcome.Applied or ProcessActionOutcome.TerminationRequested,
                "Terminate did not return an applied effect or a truthful termination request.");
            await WaitExitedAsync(child, token);
            Require(GetExitCodeProcess(child.Handle, out uint exit) && exit == 1067, "Original handle did not confirm the helper termination exit code.");
            var shortChild = await StartChildAsync(token);
            await shortChild.Process.StandardInput.WriteLineAsync("continue".AsMemory(), token); await shortChild.Process.StandardInput.FlushAsync(token);
            Require(await shortChild.ReadLine().WaitAsync(TimeSpan.FromSeconds(5), token) == Resumed, "Short gated child did not acknowledge its own input.");
            await shortChild.Process.StandardInput.WriteLineAsync("finish".AsMemory(), token); await shortChild.Process.StandardInput.FlushAsync(token);
            await WaitExitedAsync(shortChild, token);
            Require(GetExitCodeProcess(shortChild.Handle, out uint shortExit) && shortExit == 42, "Original short-child handle did not confirm exit 42.");
            var ended = (await SendAsync(shortChild, Request(shortChild, ProcessActionCommand.Query), token, alive: false)).Results.Single();
            Require(ended.Outcome == ProcessActionOutcome.AlreadyExited && ended.Flags.HasFlag(ProcessActionFlags.ExitedAfter)
                && !ended.Flags.HasFlag(ProcessActionFlags.MayHaveApplied), "A held exited lifetime was mistaken for a new live target.");
            Require(FileHash(executable) == guiHash && FileHash(helper) == helperHash, "GUI/helper artifact changed during the actions check.");
            bodyPassed = true;
        } catch (Exception error) { Log("FAIL actions-check " + error); }
        finally {
            client.Dispose();
            foreach (var child in children.Where(child => !child.Assigned)) {
                if (WaitForSingleObject(child.Handle, 0) == 258 && !AbortOwnedChild(child.Handle, 0xffff)) Log("FAIL abort unassigned owned child " + Marshal.GetLastPInvokeError());
            }
            childJob?.Dispose(); childJob = null;
            long began = Stopwatch.GetTimestamp(); bool reported = false;
            while (client.IsBusy || children.Any(child => WaitForSingleObject(child.Handle, 0) != 0)) {
                if (!reported && Stopwatch.GetElapsedTime(began) > TimeSpan.FromSeconds(5)) {
                    reported = true; bodyPassed = false; Log("FAIL cleanup still pending; keeping the real GUI parent alive until worker lease and owned handles prove exit.");
                    status.Text = "Čekám na potvrzený konec vlastního pracovníka a testovacích procesů. Výsledek je neúspěšný.";
                }
                await Task.Delay(100);
            }
            foreach (var child in children) {
                try { string error = await child.Error.WaitAsync(TimeSpan.FromSeconds(1)); if (!string.IsNullOrWhiteSpace(error)) Log("child stderr " + error); }
                catch (Exception error) { bodyPassed = false; Log("FAIL child stderr completion " + error.Message); }
                child.Process.Dispose();
            }
            bool passed = bodyPassed && cpuSetsCovered && !cancellation.IsCancellationRequested;
            Log($"{(passed ? "PASS" : bodyPassed && !cpuSetsCovered ? "PARTIAL" : "FAIL")} actions-check finished;passed={passed};baseChecksPassed={bodyPassed};cpuSetsCovered={cpuSetsCovered};cleanup=True;workerLease=False;checks={checks};cancelled={cancellation.IsCancellationRequested};mutatingDialogsCovered=False");
            Environment.ExitCode = passed ? 0 : bodyPassed && !cpuSetsCovered ? 2 : 1;
            running = false; allowClose = true; cancellation.Dispose(); Close(); Application.Current.Exit();
        }
    }
    private void CheckOrdinaryToken()
    {
        using var current = Process.GetCurrentProcess(); using var identity = WindowsIdentity.GetCurrent();
        Require(!identity.IsSystem && current.SessionId > 0, "Actions check requires an ordinary interactive GUI caller.");
        if (!OpenProcessToken(current.SafeHandle, 8, out var token)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        using (token) {
            nint data = Marshal.AllocHGlobal(4096);
            try {
                Require(GetTokenInformation(token, 20, data, 4096, out _) && Marshal.ReadInt32(data) == 0, "Actions check refuses an elevated caller.");
                Require(GetTokenInformation(token, 25, data, 4096, out _), "Token integrity is unavailable.");
                string sid = new SecurityIdentifier(Marshal.ReadIntPtr(data)).Value;
                uint level = uint.Parse(sid[(sid.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                Require(level >= 0x2000 && level < 0x3000, "Actions check requires medium integrity.");
                Log($"token user={identity.User?.Value};session={current.SessionId};integrity={level};elevated=False");
            } finally { Marshal.FreeHGlobal(data); }
        }
    }
    private static string FileHash(string file) { using var input = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(input)); }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits {
        public long ProcessTime, JobTime; public uint Flags; public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits {
        public BasicLimits Basic; public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)] private static partial SafeFileHandle CreateJobObject(nint attributes, nint name);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetInformationJobObject(SafeFileHandle job, int info, in JobLimits value, uint size);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessTimes", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetTimes(SafeProcessHandle process, out ulong created, out ulong ended, out ulong kernel, out ulong user);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial uint GetPriorityClass(SafeProcessHandle process);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetProcessAffinityMask(SafeProcessHandle process, out nuint mask, out nuint system);
    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessDefaultCpuSets", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static unsafe partial bool GetDefaultCpuSets(SafeProcessHandle process, uint* values, uint count, out uint required);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [LibraryImport("kernel32.dll", EntryPoint = "TerminateProcess", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool AbortOwnedChild(SafeProcessHandle process, uint exitCode);
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeFileHandle token);
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetTokenInformation(SafeFileHandle token, uint kind, nint data, uint size, out uint required);
}
