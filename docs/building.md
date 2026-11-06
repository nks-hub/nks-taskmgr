# Building and packaging

Back to [README](../README.md).

To build from a checked-out repository:

```powershell
pwsh -NoProfile -File scripts/build.ps1 -Configuration Release
```

The executables are placed in `build/msvc/Release`. The Win32 candidate has a DirectComposition renderer through D3D11, DXGI, and Direct2D. It shares drawing code with a DC fallback and reports the selected rendering path.

The main C#/WinUI application uses the native DLL. Build it with .NET SDK 10.0.403:

```powershell
pwsh -NoProfile -File scripts/build.ps1 -Configuration Release -WinUI -Test
./build/winui-aot/NksTaskMgr.WinUI.exe
```

Use `-DotNet` with an SDK executable path when the required SDK is installed separately. This check validates Native AOT, live data, process identities, filtering, tree traversal, collection while minimized, CPU chart layout, and read-only Services. Visual checks require an active display; captures made while the display is off can contain a black client area. The main application uses C#/WinUI Native AOT with the C++ core.

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

The script restores Windows App SDK 2.5.1 and C++/WinRT 2.0.250303.1 and writes an unpackaged output to `build/winui-cpp/app`, including the native pair, UI resources, and MSVC runtime DLLs. It builds without launching the application. This technical client implements the process, resource, and service views for comparison. The shipping application is C#/WinUI Native AOT with the C++ core; expanding another UI to all of its features is outside the product scope.

To build an installer and a portable ZIP from the published WinUI application, use Inno Setup 7.1.1:

```powershell
pwsh -NoProfile -File scripts/package.ps1 -Compiler ISCC.exe
```

Use `-Compiler` with the compiler's full path if it is outside `PATH`. `-AppDirectory` selects a different publish directory. Packages are written to `build/packages` and contain the Native AOT application, Windows App SDK files, MSVC 14.44 DLLs, and license texts. The installer uses the current user's `LocalAppData/Programs/NKS Task Manager` directory and registers a Start menu shortcut and an uninstaller. Development packages are unsigned unless signing is explicitly requested.

To sign a build, select an existing code-signing certificate in `Cert:\CurrentUser\My` and an explicit Microsoft-signed `signtool.exe`. Pass `-SigningCertificateThumbprint` and `-SignTool` to both build and package scripts. The build derives the broker and update SPKI pins from that certificate; packaging checks the public build evidence and signs the NKS binaries and setup. Third-party DLL signatures are preserved. `-TimestampServer` accepts an explicit HTTPS timestamp URL. Private keys are not exported.

The build script discovers Visual Studio and the installed Windows SDK. It sets the CRT root only for its child build processes and restores the previous environment afterward.
