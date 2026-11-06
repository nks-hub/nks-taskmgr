# NKS Task Manager

[![CI](https://github.com/nks-hub/nks-taskmgr/actions/workflows/ci.yml/badge.svg)](https://github.com/nks-hub/nks-taskmgr/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/nks-hub/nks-taskmgr?include_prereleases)](https://github.com/nks-hub/nks-taskmgr/releases)
![Development](https://img.shields.io/badge/status-in%20development-8b6f47)
![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-0078d4)
![C++](https://img.shields.io/badge/C%2B%2B-20-00599c)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![License](https://img.shields.io/badge/license-MIT-green)

A Windows process and system monitor with a C#/WinUI 3 Native AOT interface and a C++20 core.

Users launch NKS Task Manager once. The application loads its native DLL and manages its helper processes automatically. The interface is in Czech. Remaining development and acceptance work is tracked in [TODO.md](TODO.md).

![Performance view with CPU graph and device list](docs/screenshots/performance.png)

## Features

- 🔎 **Process collection** - Reads process identities, parent PIDs, CPU times, threads, handles, and I/O counters through the Windows native API.
- 🖥️ **WinUI interface** - Provides a system overview, virtual process table, CPU and memory bars, name/PID search, column sorting, multiple selection, and a selection detail panel.
- 🌳 **Process views** - Switches between all processes, measured CPU or I/O activity, application groups, and a collapsible tree that checks parent creation times before linking processes.
- 🧵 **WinUI process details** - Reads threads and modules in a separate worker and checks the selected module file's signature on request, including local catalog signatures.
- 🗂️ **Memory and objects** - Lists virtual memory regions and process handles, queries a selected object's type and name, and searches accessible processes with bounded batches and cancellation.
- 🪪 **Optional elevation** - Uses an explicit process-action menu and a publisher-pinned broker. Unsigned development builds refuse this path before requesting UAC.
- 🔐 **Capability status** - Shows observed provider states and recent target-specific query results, with explanations of permissions and driver support.
- 🧰 **Process actions** - Requests window closure, termination, suspension, and resumption after confirmation. Priority and single-group affinity changes use the same verified process handle and report readback results.
- 🌲 **Process tree termination** - Builds a bounded descendant plan from verified process lifetimes, attempts children before their parent, and reports each target and any uncertain remainder. The operation is not atomic.
- 🔧 **Service control** - Previews active dependencies, keeps the opened service objects through confirmation, and reports the observed result of start, stop, or restart.
- 💽 **Process dumps** - Uses separate x64 and x86 helpers for minidumps and optional full-memory dumps. The client publishes a verified temporary file without replacing an existing destination.
- 🧾 **Dump recovery** - Lists journaled unfinished captures on request and verifies file identity before offering completion or removal.
- 📦 **Local updates** - Verifies publisher-pinned installers, retains a verified package for rollback, and hands installation to a separate worker after the GUI exits.
- 🕒 **Process events** - Starts an ETW session on request and lists process start and stop events, provider losses, queue overflow, and decoding failures. Historical event rows remain read-only.
- 📊 **Device view** - Lists individual disks, hardware GPUs, and physical network adapters, including disconnected adapters. Each item opens a graph and current counters.
- 🌡️ **Disk detail** - Shows response time, queue length, IOPS, capacity, interface, and supported disk temperatures. Expanded NVMe health includes wear, spare capacity, operating hours, and media errors.
- 🛠️ **Services** - Lists accessible Windows services with state filters and name, account, or PID search. The selected detail shows configuration, dependencies, and a link to a host process whose creation time has been verified.
- ⏱️ **History** - Stores up to 2,401 samples for each of 512 series, draws gaps, and shows the retained duration. The parent captures samples before handing the latest snapshot to the UI.
- 🧱 **Collector isolation** - Uses separate child processes for process, device, and service queries. Each request has a five-second response deadline and a bounded local protocol; cancellation closes that child's job.
- 🧩 **CPU graphs** - Switches between total CPU, a graph for each logical processor, and overlaid processor graphs. Processor groups retain separate identities.
- 🎮 **GPU detail** - Charts individual GPU engines, dedicated memory, and shared memory. Adapter memory counters provide usage; DXGI provides capacities. Supported drivers also provide temperature through D3DKMT, refreshed every five seconds.
- ↕️ **Network traffic** - Shows receive and transmit rates in the resource list, selected chart, and device details.
- 💾 **Pagefile usage** - Displays the current and peak pagefile allocation, its capacity, and a usage graph beside physical memory and commit values.
- 📄 **WinUI snapshots** - Exports selected process and device metrics to JSON, with a preview and separate opt-ins for names and device identifiers.
- 🔍 **Process comparison** - Compares process lifetimes and metric changes between two JSON snapshots, with status filters and reasons for entries that cannot be compared.
- 🧮 **Sample states** - Distinguishes a first sample, measured zero, unavailable data, and gaps after counter or topology changes. Graph context reports units, normalization, and the calculation interval when the provider exposes it.
- 🧠 **Memory accounting** - Keeps working set, private memory, physical memory, and commit values separate.
- 🔗 **Shared collector** - Exposes immutable process and device snapshots through a versioned C API and C# bindings for Native AOT clients.
- 🧪 **Local verification** - Tests malformed buffers, PID reuse, CPU normalization, and a controlled CPU and memory load.
- 📈 **Collection benchmark** - Reports median, p95, and maximum duration together with the benchmark process memory usage.

## Requirements

- Windows 10 or Windows 11, x64.
- The desktop runs without elevation. Access to protected processes or unsupported device sensors can be unavailable.
- Visual Studio 2022 with the C++ desktop workload, MSVC 14.44, and the x64 and x86 compiler tools and SDK libraries.
- Windows SDK 10.0.26100.0, including the Universal CRT.
- Visual Studio CMake tools, CMake 3.31 or later.
- PowerShell 7.2 or later.
- .NET SDK 10.0.403 for the optional C# bindings and Native AOT interop check.

Windows 10 runtime verification and management of 32-bit processes are implementation requirements. The current native collector has been exercised on Windows 11 x64; this does not establish the final application compatibility matrix.

## Installation

Download `NksTaskMgr-<version>-<revision>-win-x64-setup.exe` or the portable ZIP from [Releases](https://github.com/nks-hub/nks-taskmgr/releases). The installer needs no administrator rights. Release builds come from CI and are not code-signed, so Windows SmartScreen asks for confirmation on first start, and the elevated broker and updater stay disabled.

To build from a checked-out repository:

```powershell
pwsh -NoProfile -File scripts/build.ps1 -Configuration Release
```

The executables are placed in `build/msvc/Release`. The Win32 candidate has a DirectComposition renderer through D3D11, DXGI, and Direct2D. It shares drawing code with a DC fallback and reports the selected rendering path. Runtime acceptance, device-loss recovery, and comparison with the WinUI 3 candidates remain open.

The main C#/WinUI application uses the native DLL. Build it with .NET SDK 10.0.403:

```powershell
pwsh -NoProfile -File scripts/build.ps1 -Configuration Release -WinUI -Test
./build/winui-aot/NksTaskMgr.WinUI.exe
```

Use `-DotNet` with an SDK executable path when the required SDK is installed separately. This check validates Native AOT, live data, process identities, filtering, tree traversal, collection while minimized, CPU chart layout, and read-only Services. Visual checks require an active display; captures made while the display is off can contain a black client area. Runtime acceptance of administrative actions and the remaining requirements is tracked in TODO. The main application uses C#/WinUI Native AOT with the C++ core.

Sentry reporting is optional and configured at build time:

```powershell
pwsh -NoProfile -File scripts/build.ps1 -Configuration Release -WinUI `
    -SentryDsn $env:NKS_SENTRY_DSN -SentryEnvironment production
```

Omitting `-SentryDsn` excludes the Sentry SDK and its initialization. `-SentryRelease` overrides the default application version and source revision. Keep deployment values outside the repository. Enabled builds report managed GUI and startup exceptions through Sentry 6.12.1; native C++ crashes and memory dumps are not integrated. Tracing, profiling, logs, and breadcrumbs are disabled. The SDK includes its own .NET and OS context; the application does not attach the monitored process inventory, command lines, or dumps.

The C++/WinUI comparison candidate uses the same native monitor and collector. Build it with Visual Studio C++ tools:

```powershell
pwsh -NoProfile -File scripts/build-winui-cpp.ps1 -NativeDirectory ./build/msvc/Release
```

The script restores Windows App SDK 2.5.1 and C++/WinRT 2.0.250303.1 and writes an unpackaged output to `build/winui-cpp/app`, including the native pair, UI resources, and MSVC runtime DLLs. It builds without launching the application. This technical client implements the process, resource, and service views for comparison. The shipping application is C#/WinUI Native AOT with the C++ core; expanding another UI to all of its features is outside the product scope. Runtime behavior and a comparison using equivalent workloads remain open.

To build an installer and a portable ZIP from the published WinUI application, use Inno Setup 7.1.1:

```powershell
pwsh -NoProfile -File scripts/package.ps1 -Compiler ISCC.exe
```

Use `-Compiler` with the compiler's full path if it is outside `PATH`. `-AppDirectory` selects a different publish directory. Packages are written to `build/packages` and contain the Native AOT application, Windows App SDK files, MSVC 14.44 DLLs, and license texts. The installer uses the current user's `LocalAppData/Programs/NKS Task Manager` directory and registers a Start menu shortcut and an uninstaller. Development packages are unsigned unless signing is explicitly requested.

To sign a build, select an existing code-signing certificate in `Cert:\CurrentUser\My` and an explicit Microsoft-signed `signtool.exe`. Pass `-SigningCertificateThumbprint` and `-SignTool` to both build and package scripts. The build derives the broker and update SPKI pins from that certificate; packaging checks the public build evidence and signs the NKS binaries and setup. Third-party DLL signatures are preserved. `-TimestampServer` accepts an explicit HTTPS timestamp URL. Private keys are not exported. Real signing and installation acceptance remain pending.

## Screenshots

| Processes | Services |
| --- | --- |
| ![Process table with search, columns and toolbar](docs/screenshots/processes.png) | ![Services with state filters and shared PID filter](docs/screenshots/services.png) |
| **Hardware** | **Startup** |
| ![Hardware view with CPU topology, memory and firmware](docs/screenshots/hardware.png) | ![Startup items with publisher signature and source key](docs/screenshots/startup.png) |

Captured on a Windows 11 virtual machine under a standard account.

## Usage

Open the main application after building with `-WinUI`:

```powershell
./build/winui-aot/NksTaskMgr.WinUI.exe
```

Switch between Processes and Performance using the left navigation. Performance keeps individual resource previews beside the graph, hardware facts, and process cards. Select a process card to open its row in the process table. The refresh control switches between one second and 250 milliseconds; Pause stops both collectors. Graphs switch between 60 seconds and 10 minutes. Ctrl+F opens the process search.

Processes provides All, Active, and Tree views. Search accepts process names and PIDs, including case-insensitive Unicode names. Click a column heading to sort, drag headings to reorder columns, and use Ctrl or Shift to select multiple rows. Tree branches expand by clicking their arrow or pressing the left and right arrow keys. Overview cards open the corresponding system resource.

The WinUI candidate also resizes columns by dragging their edges and hides optional columns through the Columns menu. A heading's context menu offers move and resize commands for keyboard use. The menu restores the default layout; the process name remains visible.

The save icon in the WinUI process toolbar opens a snapshot preview. Choose CPU, memory, process I/O, and device fields. Process and device names and raw device identifiers have separate opt-ins and are excluded by default. Command lines, arguments, accounts, and hardware fact strings are not exported. Values retain their units, sources, sample states, and process lifetime keys. The sample is captured before the dialog opens; live updates do not change it. Saving runs outside the UI thread and preserves an existing destination's access rules.

The comparison icon opens a separate window. Select the original and following JSON snapshots, then choose Compare. Their order follows your selection, even if the recorded wall clock moves backwards. The result lists process changes and shows the selected process's values, sample states, sources, and differences. Numeric differences require valid values with matching units and sources. When a process lifetime cannot be verified, the result explains why it cannot be compared. The window is read-only, accepts exports without names, and can cancel loading or comparison.

Process rows are 34 DIP high with 13 DIP text. The UI uses Segoe UI Variable when installed and Segoe UI otherwise. WinUI reads executable icons through up to two child processes after verifying process creation time. Waiting for a response is limited to three seconds; a silent worker is terminated and restarted for the next request. The workers inherit the user's permissions and exit with the application. Unavailable icons use a generic symbol, and the cache holds up to 128 images. The Win32 candidate currently uses letter icons.

While the pointer is over a process row, the table keeps its row order and updates the counters. New processes appear after the pointer leaves. A finished process remains marked until then and cannot become a live selection. Switching to Performance and back preserves the scrolled process position.

The memory page separates physical memory, commit, and pagefile usage. Pagefile usage comes from `EnumPageFilesW`; it is not a measure of paging I/O activity. Network graphs show receive and transmit values in bits per second.

WinUI graphs label their value and time scales. Auto scale follows the highest retained sample; disabling it restores the device capacity or 100 percent. Hover reads measured samples, click pins a time, and the arrow keys move through history. Enter toggles pinning and Escape returns to the live graph. Logical CPU charts adapt to the processor count and panel width.

Detailed graphs share their cursor and pinned time. Shift-click or Shift-Enter selects two interval endpoints; Escape clears the selection. Receiving charts show a cursor line without opening extra tooltips. The Processes at time button opens a separate historical view only on request. It displays retained process samples and their actual timestamps, omitted data, and gaps. Process I/O is not attributed to the selected physical disk, network adapter, or GPU. Historical rows never invoke live process actions.

The WinUI Hardware view (Sestava) lists CPU topology, caches and CPUID fields, SMBIOS memory speed and slots, motherboard and BIOS names, and UPS data exposed by the Windows battery driver. Firmware omissions remain unavailable. UPS polling runs on the resource collector every five seconds and uses read queries. SPD timings and vendor-specific UPS protocols are not implemented. Network details distinguish link speed from current receive and transmit traffic.

GPU details use the busiest engine for overall utilization. The WinUI view selects four engine graphs independently; the Win32 view lists all available engines. Memory graphs separate dedicated and shared usage. Process cards appear under CPU and physical memory; disk, network, and GPU details do not substitute CPU rankings for device activity.

Disk performance counters refresh with the device view. SMART and temperature queries refresh every 30 seconds and open devices with no read/write access requested. NVMe health comes from the standard log page; other supported drives expose failure prediction and storage temperature queries. Missing or denied data stays unavailable. These queries do not install drivers or request elevation.

Services runs in read-only mode. Its collector starts when the view opens and refreshes state once per second; polling pauses while the window is minimized. List configuration is cached for up to 15 seconds. The selected configuration is queried again with its detail. The dependency list opens a known service on double-click or Enter. Windows may omit services that the current account cannot query; denied configuration and host identity queries remain unavailable.

The WinUI Shared PID filter shows services that report the same nonzero host PID. The selected detail lists the other reported services, their states, and their accounts. This grouping follows the SCM snapshot; opening a live host process separately requires its verified lifetime identity.

Process details opens from the WinUI process toolbar for a selected live process. Its thread and module lists are read-only. File signature checks run only when requested for a selected module and use cached local trust data. The worker has the same permissions as the application, a bounded request queue, a five-second operation deadline, and cancellation that closes its own job. Runtime, WOW64, and visual acceptance of these additions remain pending.

The Memory and Handles tabs filter their loaded data. A handle's type and name are queried on request and describe the duplicated object observed at that time. Global object search starts explicitly from the process toolbar and reports its progress, skipped owners, and limits. It matches local DOS and NT path forms, batches results, and marks incomplete searches. Opening a live owner requires its full current process lifetime key. Repeated queries share at most two worker processes with two queued requests.

The Hardware view includes a capability panel. It shows current measurement states, the last targeted read query, and the application's effective elevation state. Selecting a feature explains ordinary access, elevated access, and the required provider. CPU temperature has an opt-in reader for a local HTTP API compatible with LibreHardwareMonitor. It reads timestamped CPU temperature metrics from 127.0.0.1:8085 at most once every five seconds while enabled, without starting a provider, driver, or elevated process. The provider identity is not authenticated. The view names the source, sensor ID, publication time, and age; missing or stale data is not a measured zero. The endpoint follows the [upstream HTTP contract](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/1d74d0ea012cacbcdf858ab9c5dd8a862b25d6e6/LibreHardwareMonitor.Windows.Forms/Utilities/HttpServer.cs). Actual provider and sensor acceptance remain pending. Runtime and permission acceptance of these additions remain pending.

The Applications view inventories visible windows only while that view is selected. It verifies each window owner's process lifetime and separates packaged application owners from their frame hosts. Ambiguous matches are skipped and reported as incomplete. Selecting a group keeps its member process table on the same screen; working sets are not summed into a group memory total. Runtime and visual acceptance remain pending.

The native frame API also supplies added, changed, and removed process keys with a revision and base revision. WinUI applies these batches to existing row objects and rebuilds ordering only when membership or a relevant sort or filter value changes. The Win32 table merges changed order keys and redraws affected rows without clearing an unchanged selection. A missing baseline or an older DLL falls back to the full snapshot. Full frame data remains available for other consumers. Runtime selection, scrolling, and performance acceptance remain pending.

The native DLL and desktop require `nks-collector-worker.exe` beside their binaries. Process, device, and service workers inherit the application's permissions and each run in a separate job with a 256 MiB process memory limit. The parent retains CPU sampling state and bounds replies to 64 MiB. It waits for a retiring worker to exit before starting its replacement. Timeout, cancellation, restart, and performance acceptance remain pending.

The WinUI process toolbar opens an ETW event window. Opening the window starts no session. Starting a log replaces its displayed history and may fail if Windows denies access. Process start and stop events use the manifest payload creation time when available; an event PID alone never becomes a current process lifetime key. The transfer view upserts observed disk, TCP, and UDP windows by sample ID and attribution epoch. Correlated or unknown ownership stays partial and does not establish a process total or a valid rate. The source bounds identity and transfer storage to 8 MiB and reports loss, eviction, late events, and resynchronization. The UI bounds retained rows and its estimated data budget. A failed stop retains the session lease and its cleanup state; the application waits up to five seconds before offering a close with unconfirmed cleanup. Runtime, permission, and shutdown acceptance remain pending.

The lifetime view joins start and stop events only when their verified boot ID, PID, and creation time match within one capture segment. Unverified events remain separate. Duration requires a valid pair of monotonic timestamps; an absent stop event does not establish that the process is still running. Rows report gaps, late events, duplicate or conflicting endpoints, and available exit codes. The lifetime store holds at most 4096 rows; all three modes and the held view share an estimated 11 MiB data budget. Runtime acceptance remains pending.

Process actions use `nks-actions-worker.exe` in the application's directory, with the same permissions as the application. An ordinary request contains at most 64 full process lifetime keys and creation times. The helper checks its actual parent, target identity, owner, session, critical status, and protection state before each action. It refuses system, protected, critical, and different-user or different-session targets. Tree termination accepts one root and keeps a plan of at most 256 lifetimes across three bounded inventory passes. It reports unattempted targets, limits, and uncertain remainder separately, and never claims atomic tree termination. The client permits one outstanding request and waits at most five seconds; a timeout can leave the action outcome unknown and is never retried automatically. A retiring helper must exit before another request starts. Runtime and safety acceptance remain pending.

The process action menu also edits default CPU Sets across processor groups, with a separate reset option. A preview shows group and logical-processor numbers and the current selection. The worker binds the selection to the process lifetime, topology, and previous defaults, then reads back the result. Default CPU Sets respect explicit thread selections and hard affinity; they do not prove where a thread ran. See the [Windows CPU Sets documentation](https://learn.microsoft.com/en-us/windows/win32/procthread/cpu-sets). Runtime and multi-group acceptance remain pending.

The separate elevated process-action menu uses `nks-elevation-broker.exe`. The GUI, bridge, and elevated server check the configured publisher key, signed image identity, actual peer processes, and local logon session. Each elevated batch requires its own administrator confirmation. System, critical, protected, and different-session targets remain refused. A build without a publisher pin refuses the request before starting the bridge or UAC. Configure the public SHA-256 SPKI pin through `scripts/build.ps1 -BrokerPublisherSpkiSha256 <64-hex-digits>`; the script forwards the same value to native code and WinUI assembly metadata. The application and broker must also carry valid signatures from that publisher. Signing and runtime acceptance remain pending.

Service control uses a separate `nks-service-actions-worker.exe`. Its read-only plan holds up to 64 service objects while the user confirms the operation and active dependents. Applying the plan checks the configuration and state again, stops dependents before their parent, and observes service checkpoints within a shared 30-second deadline. Restart restores the approved services that were previously running; paused or pending dependents are refused. Configuration changes, failed operations, and incomplete transitions are reported without automatic retry. Runtime and permission acceptance remain pending.

The dump command uses the operating system's DbgHelp through `nks-dump-worker.exe` or `nks-dump-worker-x86.exe`, matching the selected process architecture. Both helpers link the C++ runtime statically. Full-memory capture is off by default. The client holds the destination directory and a uniquely created temporary file; the worker receives a duplicate of that file handle and returns its identity, size, and SHA-256. Publication follows complete response validation and helper exit, uses a flush and rename without replacement, and never deletes a file by a guessed path. Capture has a 60-second deadline and a 32 GiB file limit. Runtime, debugger readability, and cancellation acceptance remain pending.

The Dump menu also opens unfinished captures without requiring a selected process. A private journal records the temporary file and helper identity before capture and records the verified hash before publication. Recovery runs only on request. It preserves unknown or unverifiable entries, refuses changes while the original GUI or helper may still be alive, and asks before completing or removing an owned temporary file. A published final dump has no recovery deletion path. Journal storage is limited to 64 entries and 1 MiB. Crash and cancellation scenarios remain to be verified in the final test phase.

The application does not expose a plugin API or scan extension folders. Helpers accept only their fixed protocol commands. Their runtime DLL search uses the application directory and Windows system directory; the dump helpers use only the system directory. Linker flags also restrict static import resolution. This follows the documented [Windows DLL search controls](https://learn.microsoft.com/en-us/cpp/build/reference/dependentloadflag). Packaged helpers are not a replacement for the signing and update checks still tracked in TODO.

The Updates button opens the local installer inventory. Adding an installer verifies and caches it without installing. A prepared update requires the signed installer for the current exact version and revision as a rollback baseline. Confirmation names the current, selected, and rollback versions. The worker waits up to 30 seconds for the GUI to exit before launching the held, verified setup in the original installed or portable mode. It does not request elevation or force other processes to close. Installer success is followed by a check of the installed application. Settings outside the installation directory are preserved.

The updater uses a separate `UpdatePublisherSpkiSha256` build property and native `NKS_UPDATE_PUBLISHER_SPKI_SHA256` value. An empty pin refuses update operations before a helper starts. The signing workflow sets both publisher pins from the selected certificate. The installer attempts a cache-only registration of itself after installation; unsigned development installs remain usable if that step is refused. Portable ZIPs include a product marker. Portable updates require that marker, preserve the selected directory, and create no uninstaller registration or application shortcuts. Registered installations require the actual current-user Inno registration. Plans and rollback history bind the directory volume and file ID; an unrecognized root remains read-only.

Unresolved operations appear in a separate recovery selector. Recovery requires an explicit plan using the verified original installer. A live or unverifiable worker or installer prevents repair. New journals store the installer lifetime before its suspended process is resumed; journal publication uses a flushed temporary file and a rename without replacement. Only a new verified repair resolves the old record, including a chain of failed repairs. Legacy records without an installer identity remain read-only in the same Windows boot. Runtime acceptance of these paths remains pending.

Cache cleanup shows a preview before a separate confirmation. The worker retains current and previous packages for known application roots, live operations, recovery evidence, and unknown data. It rechecks owned files through held handles before deletion and skips changed or busy items. Results distinguish confirmed deletion, skipped items, and unknown sizes. Cleanup does not close the GUI or launch an installer. Runtime acceptance of deletion and interruption remains pending.

The WinUI Appearance menu selects the system, light, or dark appearance and saves the choice in the user profile. Theme changes update existing controls, charts, and dialogs. Runtime and visual acceptance of the theme changes remain pending. Elevation and accessibility acceptance remain open. Missing or stale measurements are labeled rather than displayed as measured zero. The Performance view can include virtual network interfaces and loopback; the default view keeps physical adapters, including disconnected ones. This view filter preserves LUID identities and retained history. WinUI saves the preference in the user profile. Runtime acceptance of the network filter remains pending.

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

The build script discovers Visual Studio and the installed Windows SDK. It sets the CRT root only for its child build processes and restores the previous environment afterward.

## Contributing

1. Fork the repository.
2. Create a branch for the change.
3. Commit the change with a message such as `feat: add a process filter`.
4. Push the branch to your fork.
5. Open a pull request with the behavior change and verification results.

## Support

- 📧 **Email:** dev@nks-hub.cz
- 🐛 **Bug reports:** [GitHub Issues](https://github.com/nks-hub/nks-taskmgr/issues)

## License

MIT License — see [LICENSE](LICENSE) for details.

---

<p align="center">Made with ❤️ by <a href="https://github.com/nks-hub">NKS Hub</a></p>
