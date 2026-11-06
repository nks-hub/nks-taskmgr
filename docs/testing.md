# Testing and benchmarks

Back to [README](../README.md).

Build and run the local tests:

```powershell
pwsh -NoProfile -File scripts/build.ps1 -Configuration Release -Test
pwsh -NoProfile -File scripts/build.ps1 -Configuration Debug -Test
```

The live check allocates and touches 32 MiB in its own process and runs one CPU worker for one second. It verifies that the collector sees the resulting CPU activity, memory change, and process creation time.

The process-view test uses a native ListView in a hidden test window to check selection across refresh, PID reuse, parent generations, cycles, filtering, Unicode names, column reordering, row height, held rows, and scroll restoration.

After building the native Release targets, publish and run the Native AOT interop check:

```powershell
dotnet publish tests/native_aot/NativeAotProbe.csproj -c Release -r win-x64 -o build/native-aot-probe
./build/native-aot-probe/NativeAotProbe.exe
```

This check reads process, device, and service snapshots, validates ABI layouts and process identity, and retains frames after stopping collectors and collecting managed memory. Services use a separate ABI in the same DLL; their worker stays inactive until requested.

Build the separate traffic ABI fixture and publish its AOT probe:

```powershell
cmake --build --preset release --target nks-traffic-test-api
dotnet publish tests/traffic_abi/TrafficAbiProbe.csproj -c Release -r win-x64 -o build/traffic-abi-probe
./build/traffic-abi-probe/TrafficAbiProbe.exe
```

Controlled disk observations exercise the production event store, C API serializer, and C# decoder. The probe checks malformed attribution, retained frames, and revocation after source loss. The probe loads its own fixture DLL from the probe output. Live disk capture remains a separate runtime check.

Process identity uses the full 128-bit Windows boot identifier with the PID and generation. `nks_frame_get_boot_data` returns both identifier halves and their availability state. The ABI v1 `boot_time` field remains wall-clock metadata and is not an identity key. Denied or unsupported boot queries report their state without stopping process collection.

`nks_frame_get_network_transfer_data` returns bytes received and sent per network adapter since the monitor first observed it, with the start time. Totals add only non-negative deltas of the 64-bit interface octet counters; a counter decrease, an unavailable sample or a missing adapter restarts the baseline without adding traffic.

`nks_frame_get_session_data` lists the local WTS sessions (user, domain, window station, connection state and protocol) with CPU, working set, private memory and I/O totals over each session's processes, plus a session ID per process in frame order. WTS is queried only on the first call for a frame. A sum marks how many members had a current sample; the idle process is excluded. A session that only processes reveal is reported with unknown state, and a denied user query reports `ACCESS_DENIED` instead of an empty name. The WinUI Users view (Uživatelé) shows these totals and expands a session into its processes; per-process network traffic is not attributed yet, so the network column stays unavailable.

Run the collection benchmark:

```powershell
pwsh -NoProfile -File scripts/build.ps1 -Configuration Release -Benchmark
```

The collection benchmark performs 10 warm-up collections and 200 measured collections without a sampling delay. It reports native query, parser, system counter, and calculation costs separately. It does not measure idle CPU usage, startup, or rendering.

Measure desktop CPU, private memory, working set, handles, and GDI objects:

```powershell
pwsh -NoProfile -File benchmarks/desktop_bench.ps1
pwsh -NoProfile -File benchmarks/desktop_bench.ps1 -View Processes
pwsh -NoProfile -File benchmarks/desktop_bench.ps1 -View Services
pwsh -NoProfile -File benchmarks/desktop_bench.ps1 -Candidate WinUI -View Services
pwsh -NoProfile -File benchmarks/desktop_bench.ps1 -Candidate WinUI -View Performance -Resource 'GPU 0'
```

This local benchmark opens its own application instance, warms it up for 10 seconds, then measures 60 seconds visible and 60 seconds minimized. It requires an active display and stops if the visible window loses foreground. `-Candidate WinUI` selects the AOT build; its Services scenario selects the first visible service and loads its detail. Results, including revision and binary hashes, are written to the system temporary directory. CPU is normalized to the whole machine. Acceptance targets and open findings are maintained in TODO.md; these measurements do not replace the planned OS, UI candidate, or 24-hour tests.

The desktop `--smoke` option checks view switching, retained process selection, PID filtering, service collection and detail, and stale data after stopping both process and device collectors. It exits with code 0 on success.

Display checks use the current interactive session. The console display state is recorded separately, since a remote desktop can be active while the physical monitor is off.

Measure WinUI process sorting with live rows and a 10,000-row replay:

```powershell
./build/winui-aot/NksTaskMgr.WinUI.exe "--bench-sort=$env:TEMP\nks-sort.csv"
```

This check covers every column in both directions, retains multiple selections and the primary process, preserves the scrolled identity, and checks reuse of visible row visuals. The CSV separates handler and layout time from two subsequent rendering callbacks and counts icon decodes started during each case. Those callbacks do not prove hardware presentation time. The benchmark closes its own window when finished; failures produce a sibling `.error.txt` file and a nonzero exit code.

The same run also measures process search (name prefix, PID digits, no match, clear) and selection (visible click, one-row and five-row steps, jump requiring scroll), 20 repetitions per case on both data sets. It writes `<path>.interaction.csv` (`scenario,rows,case,iteration,handler_ms,layout_ms,two_frames_ms`) and `<path>.interaction-summary.csv` (`scenario,rows,case,samples,p95_handler_ms,max_handler_ms,p95_two_frames_ms,max_two_frames_ms,target_ms,target_met`). The targets (search 100 ms, selection 50 ms, compared with the p95 of `two_frames_ms`) are reported, not enforced; only functional errors fail the run.

UI thread CPU time and managed allocation counters cover the handler and explicit layout call. Windows CPU accounting can be coarser than the elapsed timer, so short CPU samples can exceed the measured elapsed interval. Garbage collection counts are process-wide and recorded separately for each generation. Native XAML and graphics allocations are outside the managed allocation counter.

Adding `--bench-sort-no-icons` suppresses icon loading only during this diagnostic benchmark. The CSV records the selected mode; results without icons do not validate the normal UI performance target.
