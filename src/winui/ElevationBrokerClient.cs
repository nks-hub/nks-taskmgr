using System.Diagnostics;

namespace NksTaskMgr.WinUI;

internal sealed class ElevationBrokerClient : IDisposable
{
    private readonly ProcessActionsClient client = new(elevated: true);
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private int pending;
    internal bool IsBusy => Volatile.Read(ref pending) != 0 || client.IsBusy;
    internal ElevationBrokerClient() => lifetimeToken = lifetime.Token;

    internal async Task<ProcessActionsBatch> ExecuteAsync(ProcessActionsRequest request, CancellationToken cancellation)
    {
        if (Interlocked.CompareExchange(ref pending, 1, 0) != 0)
            throw new IOException("Předchozí zvýšený požadavek ještě není uzavřený. Neopakujte jej.");
        long started = Stopwatch.GetTimestamp(); bool uncertain = true;
        try {
            var result = await client.ExecuteAsync(request, cancellation).ConfigureAwait(false);
            uncertain = result.Frame == ProcessActionFrame.Failed || result.Accepted
                && result.Frame is not (ProcessActionFrame.Results or ProcessActionFrame.TreeResults);
            return result;
        } catch (ElevationBrokerTrustException error) {
            uncertain = false;
            return new ProcessActionsBatch(request.Command, ProcessActionFrame.Rejected, error.Error, [], DateTimeOffset.UtcNow);
        } finally {
            if (uncertain) _ = ReleaseAfterBrokerDeadlineAsync(started);
            else Interlocked.Exchange(ref pending, 0);
        }
    }

    private async Task ReleaseAfterBrokerDeadlineAsync(long started)
    {
        // A medium process cannot reliably kill an elevated peer. Its native watchdog is 165 s.
        var remaining = TimeSpan.FromSeconds(170) - Stopwatch.GetElapsedTime(started);
        try { if (remaining > TimeSpan.Zero) await Task.Delay(remaining, lifetimeToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally { Interlocked.Exchange(ref pending, 0); }
    }

    internal static string Status(uint error) => error switch {
        0xe04e0001 => "TrustNotConfigured: zvýšení práv není nakonfigurované. Chybí připnutý podpis vydavatele; tento vývojový build zůstává bez zvýšení práv.",
        0xe04e0002 => "Podpis, soubor nebo skutečný protějšek nebyl důvěryhodně ověřen. Zvýšení práv bylo odmítnuto.",
        0xe04e0003 => "Vlastní potvrzení správce bylo zrušeno, vypršel jeho limit nebo už klient neběží. Akce nebyla odeslána.",
        1223 => "Zvýšení práv bylo v UAC zrušeno. Akce nebyla odeslána.",
        _ => ""
    };

    public void Dispose() { lifetime.Cancel(); client.Dispose(); lifetime.Dispose(); }
}
