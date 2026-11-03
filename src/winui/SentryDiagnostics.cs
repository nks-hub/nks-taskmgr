using System.Diagnostics;
#if NKS_SENTRY
using System.Reflection;
using Sentry;
#endif

namespace NksTaskMgr.WinUI;

internal static class SentryDiagnostics
{
#if NKS_SENTRY
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);
#endif

    [Conditional("NKS_SENTRY")]
    internal static void Initialize()
    {
#if NKS_SENTRY
        try {
            var assembly = typeof(App).Assembly;
            var configuration = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Where(value => value.Key.StartsWith("Nks.Sentry", StringComparison.Ordinal))
                .ToDictionary(value => value.Key, value => value.Value);
            if (!configuration.TryGetValue("Nks.SentryDsn", out string? dsn) || string.IsNullOrWhiteSpace(dsn)) return;
            string? release = configuration.GetValueOrDefault("Nks.SentryRelease");
            if (string.IsNullOrWhiteSpace(release))
                release = "nks-taskmgr@" + assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            SentrySdk.Init(options => {
                options.Dsn = dsn;
                options.Environment = configuration.GetValueOrDefault("Nks.SentryEnvironment") ?? "production";
                options.Release = release;
                options.IsGlobalModeEnabled = true;
                options.SendDefaultPii = false;
                options.AutoSessionTracking = false;
                options.TracesSampleRate = 0;
                options.ProfilesSampleRate = 0;
                options.EnableLogs = false;
                options.SetBeforeSendMetric(_ => null);
                options.EnableBackpressureHandling = false;
                options.EnableSpotlight = false;
                options.MaxBreadcrumbs = 0;
                options.ShutdownTimeout = ShutdownTimeout;
                options.DisableDiagnosticSourceIntegration();
                options.DisableSystemDiagnosticsMetricsIntegration();
                // The SDK's reflective WinUI hook is disabled by Native AOT trimming.
                options.DisableWinUiUnhandledExceptionIntegration();
            });
        }
        catch (Exception error) {
            // Reporting is optional and must not prevent application startup.
            Debug.WriteLine("Sentry initialization failed: " + error.GetType().Name);
        }
#endif
    }

    [Conditional("NKS_SENTRY")]
    internal static void Capture(Exception error, string mechanism, bool handled)
    {
#if NKS_SENTRY
        if (!SentrySdk.IsEnabled) return;
        try {
            error.SetSentryMechanism(mechanism, handled: handled, terminal: !handled);
            SentrySdk.CaptureException(error);
            if (!handled) SentrySdk.FlushAsync(ShutdownTimeout).GetAwaiter().GetResult();
        }
        catch (Exception reportingError) {
            // Crash reporting must preserve the original application exception.
            Debug.WriteLine("Sentry capture failed: " + reportingError.GetType().Name);
        }
#endif
    }
}
