# NKS Task Manager

![Development](https://img.shields.io/badge/status-in%20development-8b6f47)
![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-0078d4)
![C++](https://img.shields.io/badge/C%2B%2B-20-00599c)
![License](https://img.shields.io/badge/license-MIT-green)

A Windows process and system monitor written in C++20.

Development has started with the native collector, local tests, and measurement tools. The desktop interface and administrative actions are tracked in [TODO.md](TODO.md).

## Features

- 🔎 **Process collection** - Reads process identities, parent PIDs, CPU times, threads, handles, and I/O counters through the Windows native API.
- 🧮 **Sample states** - Distinguishes a first sample, measured zero, unavailable data, and gaps after counter or topology changes.
- 🧠 **Memory accounting** - Keeps working set, private memory, physical memory, and commit values separate.
- 🧪 **Local verification** - Tests malformed buffers, PID reuse, CPU normalization, and a controlled CPU and memory load.
- 📈 **Collection benchmark** - Reports median, p95, and maximum duration together with the benchmark process memory usage.

## Requirements

- Windows 10 or Windows 11, x64.
- Visual Studio 2022 with the C++ desktop workload and MSVC 14.44.
- Windows SDK 10.0.26100.0, including the Universal CRT.
- Visual Studio CMake tools, CMake 3.31 or later.
- PowerShell 7 or later.

Windows 10 runtime verification and management of 32-bit processes are implementation requirements. The current native collector has been exercised on Windows 11 x64; this does not establish the final application compatibility matrix.

## Installation

From a checked-out repository:

```powershell
pwsh -NoProfile -File scripts/build.ps1 -Configuration Release
```

The current executables are placed in `build/msvc/Release`. The collector is a console development tool; the desktop application is still being implemented.

## Usage

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

Run the collection benchmark:

```powershell
pwsh -NoProfile -File scripts/build.ps1 -Configuration Release -Benchmark
```

The benchmark performs 10 warm-up collections and 200 measured collections without a sampling delay. It measures collector and calculation cost, not idle CPU usage, startup, or desktop rendering. Acceptance targets and open findings are maintained in TODO.md.

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
