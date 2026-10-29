# NKS Task Manager

![Development](https://img.shields.io/badge/status-in%20development-8b6f47)
![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-0078d4)
![C++](https://img.shields.io/badge/C%2B%2B-20-00599c)
![License](https://img.shields.io/badge/license-MIT-green)

A Windows process and system monitor written in C++20.

The current desktop prototype displays live processes and individual system devices. Administrative actions and the remaining application scope are tracked in [TODO.md](TODO.md).

## Features

- 🔎 **Process collection** - Reads process identities, parent PIDs, CPU times, threads, handles, and I/O counters through the Windows native API.
- 🖥️ **Desktop prototype** - Provides a system overview, virtual process table, CPU and memory bars, name/PID search, column sorting, multiple selection, and a selection detail panel.
- 🌳 **Process views** - Switches between all processes, measured CPU or I/O activity, and a collapsible tree that checks parent creation times before linking processes.
- 📊 **Device view** - Lists individual disks, hardware GPUs, and physical network adapters, including disconnected adapters. Each item opens a graph and current counters.
- 🛠️ **Services** - Lists accessible Windows services with state filters and name, account, or PID search. The selected detail shows configuration, dependencies, and a link to a host process whose creation time has been verified.
- ⏱️ **History** - Stores up to 2,401 samples for each of 512 series, draws gaps, and shows the retained duration. Collection runs on separate process and device threads with one pending snapshot each.
- 🧩 **CPU graphs** - Switches between total CPU, a graph for each logical processor, and overlaid processor graphs. Processor groups retain separate identities.
- 🎮 **GPU detail** - Charts individual GPU engines, dedicated memory, and shared memory. Adapter memory counters provide usage; DXGI provides capacities. Supported drivers also provide temperature through D3DKMT, refreshed every five seconds.
- ↕️ **Network traffic** - Shows receive and transmit rates in the resource list, selected chart, and device details.
- 💾 **Pagefile usage** - Displays the current and peak pagefile allocation, its capacity, and a usage graph beside physical memory and commit values.
- 🧮 **Sample states** - Distinguishes a first sample, measured zero, unavailable data, and gaps after counter or topology changes.
- 🧠 **Memory accounting** - Keeps working set, private memory, physical memory, and commit values separate.
- 🔗 **Shared collector** - Exposes immutable process and device snapshots through a versioned C API and C# bindings for Native AOT clients.
- 🧪 **Local verification** - Tests malformed buffers, PID reuse, CPU normalization, and a controlled CPU and memory load.
- 📈 **Collection benchmark** - Reports median, p95, and maximum duration together with the benchmark process memory usage.

## Requirements

- Windows 10 or Windows 11, x64.
- Visual Studio 2022 with the C++ desktop workload and MSVC 14.44.
- Windows SDK 10.0.26100.0, including the Universal CRT.
- Visual Studio CMake tools, CMake 3.31 or later.
- PowerShell 7 or later.
- .NET SDK 10.0.403 for the optional C# bindings and Native AOT interop check.

Windows 10 runtime verification and management of 32-bit processes are implementation requirements. The current native collector has been exercised on Windows 11 x64; this does not establish the final application compatibility matrix.

## Installation

From a checked-out repository:

```powershell
pwsh -NoProfile -File scripts/build.ps1 -Configuration Release
```

The executables are placed in `build/msvc/Release`. The desktop uses Win32 and Direct2D as the first UI candidate. The planned WinUI 3 and Native AOT comparison remains open.

The experimental C#/WinUI candidate shares the native DLL. Build it with .NET SDK 10.0.403:

```powershell
pwsh -NoProfile -File scripts/build.ps1 -Configuration Release -WinUI -Test
./build/winui-aot/NksTaskMgr.WinUI.exe
```

Use `-DotNet` with an SDK executable path when the required SDK is installed separately. This check validates Native AOT, live data, process identities, filtering, tree traversal, collection while minimized, CPU chart layout, and read-only Services. Visual checks require an active display; captures made while the display is off can contain a black client area. Administrative actions and the UI framework choice remain open.

## Usage

Open the desktop prototype:

```powershell
./build/msvc/Release/nks-taskmgr.exe
```

Switch between Processes and Performance using the left navigation. Performance keeps individual resource previews beside the graph, hardware facts, and process cards. Select a process card to open its row in the process table. The refresh control switches between one second and 250 milliseconds; Pause stops both collectors. Graphs switch between 60 seconds and 10 minutes. Ctrl+F opens the process search.

Processes provides All, Active, and Tree views. Search accepts process names and PIDs, including case-insensitive Unicode names. Click a column heading to sort, drag headings to reorder columns, and use Ctrl or Shift to select multiple rows. Tree branches expand by clicking their arrow or pressing the left and right arrow keys. Overview cards open the corresponding system resource.

The WinUI candidate also resizes columns by dragging their edges and hides optional columns through the Columns menu. A heading's context menu offers move and resize commands for keyboard use. The menu restores the default layout; the process name remains visible.

Process rows are 34 DIP high with 13 DIP text. The UI uses Segoe UI Variable when installed and Segoe UI otherwise. WinUI loads executable icons on background workers after verifying process creation time; unavailable icons use a generic symbol. Its icon cache holds up to 128 images. The Win32 candidate currently uses letter icons.

While the pointer is over a process row, the table keeps its row order and updates the counters. New processes appear after the pointer leaves. A finished process remains marked until then and cannot become a live selection. Switching to Performance and back preserves the scrolled process position.

The memory page separates physical memory, commit, and pagefile usage. Pagefile usage comes from `EnumPageFilesW`; it is not a measure of paging I/O activity. Network graphs show receive and transmit values in bits per second.

WinUI graphs label their value and time scales. Auto scale follows the highest retained sample; disabling it restores the device capacity or 100 percent. Hover reads measured samples, click pins a time, and the arrow keys move through history. Enter toggles pinning and Escape returns to the live graph. Logical CPU charts adapt to the processor count and panel width.

The WinUI Hardware view (Sestava) lists CPU topology, caches and CPUID fields, SMBIOS memory speed and slots, motherboard and BIOS names, and UPS data exposed by the Windows battery driver. Firmware omissions remain unavailable. UPS polling runs on the resource collector every five seconds and uses read queries. SPD timings and vendor-specific UPS protocols are not implemented. Network details distinguish link speed from current receive and transmit traffic.

GPU details use the busiest engine for overall utilization. The WinUI view selects four engine graphs independently; the Win32 view lists all available engines. Memory graphs separate dedicated and shared usage. Process cards appear under CPU and physical memory; disk, network, and GPU details do not substitute CPU rankings for device activity.

Services runs in read-only mode. Its collector starts when the view opens and refreshes state once per second; polling pauses while the window is minimized. List configuration is cached for up to 15 seconds. The selected configuration is queried again with its detail. The dependency list opens a known service on double-click or Enter. Windows may omit services that the current account cannot query; denied configuration and host identity queries remain unavailable.

The current UI implements the dark appearance. Administrative actions, accessibility acceptance, and the light appearance are not yet implemented. Missing or stale measurements are labeled rather than displayed as measured zero. Virtual network interfaces are not yet exposed.

Capture two real process samples one second apart:

```powershell
./build/msvc/Release/nks-sample.exe
```

The JSON result contains the process count, logical processor count, system CPU state, physical memory, and collection duration. Process I/O counters represent I/O issued by a process; they are not per-disk or network traffic measurements.

### Third-party source

The NT declarations and adapted process enumeration originate from [System Informer](https://github.com/winsiderss/systeminformer/tree/99a0d28e091878967a804ac9ea2fe22f2c1fdadb), pinned to commit `99a0d28e091878967a804ac9ea2fe22f2c1fdadb`. The retained license is in `third_party/phnt/LICENSE`. The source revision, file hashes, and adaptation are recorded in `third_party/phnt/upstream.json`. Existing copyright notices are retained.

## Testing

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

This check reads process, device, and service snapshots, validates ABI layouts and process identity, and retains frames after stopping collectors and collecting managed memory. Services use a separate ABI in the same DLL; their worker stays inactive until requested. It is a console check; the WinUI UI comparison remains open.

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

This check covers every column in both directions, retains multiple selections and the primary process, preserves the scrolled identity, and checks reuse of visible row visuals. The CSV separates handler and layout time from two subsequent rendering callbacks. Those callbacks do not prove hardware presentation time. The benchmark closes its own window when finished; failures produce a sibling `.error.txt` file and a nonzero exit code.

The build script discovers Visual Studio and the installed Windows SDK. It sets the CRT root only for its child build processes and restores the previous environment afterward.

## Contributing

1. Fork the repository.
2. Create a branch for the change.
3. Commit the change with a message such as `feat: add a process filter`.
4. Push the branch to your fork.
5. Open a pull request with the behavior change and verification results.

## Support

- 📧 **Email:** dev@nks-hub.cz
- 🐛 **Bug reports:** use the repository issue tracker after publication.

## License

MIT License — see [LICENSE](LICENSE) for details.

---

<p align="center">Made with ❤️ by <a href="https://github.com/nks-hub">NKS Hub</a></p>
