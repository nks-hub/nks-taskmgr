using System.Diagnostics;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal sealed class GlobalObjectSearchCoordinator(ProcessDetailsClient client)
{
    internal const int MaximumResults = 5000, MaximumQueries = 25000, MaximumPerOwner = 4096;
    private const int BatchSize = 40;
    private const int MaximumResultCharacters = 4 * 1024 * 1024;
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(2);

    internal Task SearchAsync(BootIdentifier boot, GlobalObjectSearchQuery query,
        Func<GlobalObjectSearchBatch, CancellationToken, Task> publish, CancellationToken cancellation)
    {
        if (!query.IsValid) throw new ArgumentException(Loc.T("Název nebo typ musí mít alespoň dva znaky."), nameof(query));
        return Task.Run(() => SearchCoreAsync(boot, query with { Name = query.Name.Trim(), Type = query.Type.Trim() }, publish, cancellation), cancellation);
    }

    private async Task SearchCoreAsync(BootIdentifier boot, GlobalObjectSearchQuery query,
        Func<GlobalObjectSearchBatch, CancellationToken, Task> publish, CancellationToken cancellation)
    {
        var state = new Counters();
        var pending = new List<GlobalObjectSearchResult>(BatchSize);
        var elapsed = Stopwatch.StartNew();
        TimeSpan lastPublish = TimeSpan.Zero;
        using var budget = new CancellationTokenSource(MaximumDuration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, budget.Token);
        async Task FlushAsync(bool force, CancellationToken token)
        {
            if (!force && pending.Count < BatchSize && elapsed.Elapsed - lastPublish < TimeSpan.FromMilliseconds(200)) return;
            var batch = new GlobalObjectSearchBatch(pending.ToArray(), state.Snapshot()); pending.Clear();
            // Awaiting delivery prevents a queue of UI batches from growing behind a busy window.
            await publish(batch, token).ConfigureAwait(false); lastPublish = elapsed.Elapsed;
        }
        try {
            var paths = GlobalObjectSearchPaths.Capture(linked.Token);
            string canonical = paths.Canonical(query.Name);
            if (!paths.CanResolve(query.Name)) { state.Partial = true; state.Note = Loc.T("Převod zadaného písmene disku není dostupný; porovnávají se i doslovné názvy."); }
            var owners = await client.ReadOwnersAsync(boot, linked.Token).ConfigureAwait(false);
            if (owners.State != ProcessDetailsState.Ok) {
                state.Partial = true; state.Note = Loc.F("Seznam vlastníků není dostupný: {0}", Status(owners));
            } else {
                state.TotalOwners = owners.Owners.Length;
                if (owners.PartialMetrics) { state.Partial = true; state.Note = Loc.T("Některé vlastníky nebylo možné spolehlivě určit."); }
                await FlushAsync(true, linked.Token).ConfigureAwait(false);
                foreach (var owner in owners.Owners) {
                    linked.Token.ThrowIfCancellationRequested();
                    if (state.ObjectsExamined >= MaximumQueries || state.Matches >= MaximumResults || state.ResultTextLimit) break;
                    ++state.ExaminedOwners;
                    state.CurrentOwner = GlobalObjectSearchText.Flat(owner.Name, 80) + " · PID " + owner.Key.Pid;
                    bool unavailable = false, ended = false, limited = false;
                    try {
                        var handles = await client.ReadHandlesAsync(owner.Key, linked.Token).ConfigureAwait(false);
                        if (handles.State != ProcessDetailsState.Ok) {
                            ended = handles.State == ProcessDetailsState.TargetChanged;
                            limited = handles.State == ProcessDetailsState.LimitExceeded;
                            unavailable = !ended && !limited;
                            state.Partial = true;
                            continue;
                        }
                        ++state.SearchedOwners;
                        int count = Math.Min(handles.Handles.Length, MaximumPerOwner), consecutiveFailures = 0;
                        if (handles.PartialMetrics || count < handles.Handles.Length) { limited = true; state.Partial = true; }
                        state.ObjectsSkipped += handles.Handles.Length - count;
                        for (int i = 0; i < count; ++i) {
                            linked.Token.ThrowIfCancellationRequested();
                            if (state.ObjectsExamined >= MaximumQueries || state.Matches >= MaximumResults) {
                                limited = true; state.Partial = true; break;
                            }
                            var handle = handles.Handles[i];
                            ++state.ObjectsExamined;
                            try {
                                var info = await client.ReadHandleInfoAsync(owner.Key, handle.Value, linked.Token).ConfigureAwait(false);
                                // A refused handle (for example a non-duplicable EtwRegistration) answers normally; only
                                // transport failures below count towards abandoning the owner.
                                if (info.State != ProcessDetailsState.Ok || info.HandleInfo is not { } observation) {
                                    state.Partial = true; ++state.ObjectsSkipped; consecutiveFailures = 0;
                                    if (info.State == ProcessDetailsState.TargetChanged) { ended = true; break; }
                                    if (info.State == ProcessDetailsState.LimitExceeded) limited = true; else unavailable = true;
                                    await FlushAsync(false, linked.Token).ConfigureAwait(false);
                                    continue;
                                }
                                consecutiveFailures = 0;
                                if (info.PartialMetrics || observation.TypeState != SampleState.Valid || observation.NameState != SampleState.Valid) {
                                    state.Partial = true; unavailable = true; ++state.ObjectsSkipped;
                                }
                                bool typeMatches = query.Type.Length == 0 || observation.TypeState == SampleState.Valid
                                    && observation.Type.Contains(query.Type, StringComparison.OrdinalIgnoreCase);
                                bool nameMatches = query.Name.Length == 0 || observation.NameState == SampleState.Valid
                                    && paths.Matches(observation.Name, query.Name, canonical);
                                if (typeMatches && nameMatches) {
                                    string display = paths.DisplayName(observation.Name);
                                    int characters = observation.Type.Length + observation.Name.Length + display.Length;
                                    if (characters > MaximumResultCharacters - state.ResultCharacters) {
                                        state.Partial = state.ResultTextLimit = limited = true;
                                        state.Note = Loc.T("Dosažen limit 8 MiB textu výsledků."); break;
                                    }
                                    state.ResultCharacters += characters;
                                    pending.Add(new(owner.Key, owner.Name, handle, observation, display, info.ReceivedAt));
                                    ++state.Matches;
                                }
                            } catch (TimeoutException) {
                                state.Partial = true; limited = true; ++state.ObjectsSkipped; ++consecutiveFailures;
                            } catch (Exception error) when (error is IOException or InvalidDataException) {
                                state.Partial = true; unavailable = true; ++state.ObjectsSkipped; ++consecutiveFailures;
                            }
                            await FlushAsync(false, linked.Token).ConfigureAwait(false);
                            if (consecutiveFailures >= 3) {
                                limited = true; state.ObjectsSkipped += count - i - 1; break;
                            }
                        }
                    } catch (TimeoutException) { limited = true; state.Partial = true; }
                    catch (Exception error) when (error is IOException or InvalidDataException) { unavailable = true; state.Partial = true; }
                    finally {
                        if (linked.IsCancellationRequested) limited = true;
                        if (unavailable) ++state.UnavailableOwners;
                        if (ended) ++state.EndedOwners;
                        if (limited) ++state.LimitedOwners;
                        await FlushAsync(false, linked.Token).ConfigureAwait(false);
                    }
                }
                if (state.Matches >= MaximumResults || state.ObjectsExamined >= MaximumQueries) {
                    state.Partial = true; state.Note = state.Matches >= MaximumResults ? Loc.T("Dosažen limit 5 000 výsledků.") : Loc.T("Dosažen limit 25 000 dotazů na objekty.");
                }
            }
        } catch (OperationCanceledException) when (linked.IsCancellationRequested) {
            state.Partial = true; state.Cancelled = cancellation.IsCancellationRequested;
            state.Note = state.Cancelled ? Loc.T("Hledání bylo zrušené.") : Loc.T("Hledání dosáhlo časového limitu 2 minut.");
        } catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or ObjectDisposedException) {
            state.Partial = true; state.Note = Loc.F("Hledání nelze dokončit: {0}", error.Message);
        }
        state.Finished = true; state.CurrentOwner = "";
        await FlushAsync(true, CancellationToken.None).ConfigureAwait(false);
    }

    private static string Status(ProcessDetailsSnapshot snapshot) => snapshot.State switch {
        ProcessDetailsState.AccessDenied => Loc.T("chybí oprávnění"), ProcessDetailsState.Unsupported => Loc.T("nepodporováno"),
        ProcessDetailsState.TargetChanged => Loc.T("proces se změnil nebo skončil"), ProcessDetailsState.LimitExceeded => Loc.T("překročen limit"),
        _ => Loc.F("nedostupné ({0})", snapshot.State)
    };

    private sealed class Counters
    {
        internal int TotalOwners = -1;
        internal int ExaminedOwners, SearchedOwners, UnavailableOwners, EndedOwners, LimitedOwners, ObjectsExamined, ObjectsSkipped, Matches, ResultCharacters;
        internal bool Partial, Finished, Cancelled, ResultTextLimit;
        internal string CurrentOwner = "", Note = "";
        internal GlobalObjectSearchProgress Snapshot() => new(TotalOwners, ExaminedOwners, SearchedOwners, UnavailableOwners,
            EndedOwners, LimitedOwners, ObjectsExamined, ObjectsSkipped, Matches, Partial, Finished, Cancelled, CurrentOwner, Note);
    }
}
