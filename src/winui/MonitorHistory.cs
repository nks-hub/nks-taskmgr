using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MonitorModel
{
    private sealed record CollectedSeries(HistoryChannel Channel, string Resource, string Detail, string Name,
        uint Group, uint Index, HistorySeries History);
    private readonly Dictionary<uint, CollectedSeries> collectedSeries = [];
    private ulong historySequence, historyLost, historyErrors;
    private bool historyUnavailable, collectedBindingsDirty = true;
    public string HistoryWarning => historyUnavailable ? Loc.T("Historie je nedostupná")
        : historyLost != 0 || historyErrors != 0 ? Loc.F("Historie: {0:N0} neuchovaných vzorků, {1:N0} chyb", historyLost, historyErrors) : "";

    private unsafe bool ReadCollectedHistory()
    {
        bool changed = false;
        try {
            using var batch = NativeHistory.Read(monitor);
            if (batch is null) { gapDiagnostics?.HistoryNoData(); BindCollectedSeries(); return false; }
            batch.Read(view => changed = ApplyCollectedHistory(view));
            changed |= historyUnavailable; historyUnavailable = false;
        } catch (Exception error) when (error is NativeMonitorException or EntryPointNotFoundException) {
            gapDiagnostics?.HistoryReadError(error);
            changed = !historyUnavailable; historyUnavailable = true; BreakCollectedSeries(GapBreakReason.HistoryReadError);
        }
        return changed;
    }

    internal unsafe bool ApplyCollectedHistory(HistoryView view)
    {
        gapDiagnostics?.Batch(view);
        for (uint i = 0; i < view.DefinitionCount; ++i) {
            var definition = view.Definitions[i];
            if (definition.Id == 0 || definition.Id > 512 || !Enum.IsDefined(definition.Channel))
                throw new NativeMonitorException(NativeResult.AbiMismatch);
            if (collectedSeries.ContainsKey(definition.Id)) continue;
            if (definition.ResourceId.Length > 1024 || definition.DetailId.Length > 1024 || definition.Name.Length > 1024)
                throw new NativeMonitorException(NativeResult.AbiMismatch);
            var series = new CollectedSeries(definition.Channel, definition.ResourceId.Copy(), definition.DetailId.Copy(),
                definition.Name.Copy(), definition.Group, definition.Index, new HistorySeries());
            collectedSeries.Add(definition.Id, series);
            gapDiagnostics?.Define(definition.Id, series.Channel, series.Resource, series.Group, series.Index);
            collectedBindingsDirty = true;
        }
        if (view.LostPoints != 0 || view.CaptureErrors != 0)
            BreakCollectedSeries((view.LostPoints != 0 ? GapBreakReason.HistoryLost : GapBreakReason.None)
                | (view.CaptureErrors != 0 ? GapBreakReason.CaptureError : GapBreakReason.None));
        historyLost += view.LostPoints; historyErrors += view.CaptureErrors;
        for (uint i = 0; i < view.PointCount; ++i) {
            var point = view.Points[i];
            if (point.Sequence == 0 || point.TimeNs <= 0 || !collectedSeries.TryGetValue(point.Series, out var series))
                throw new NativeMonitorException(NativeResult.AbiMismatch);
            if (point.Sequence <= historySequence) {
                gapDiagnostics?.Point(point, point.Metric, series.History.LastTime, false, GapBreakReason.DuplicateSequence);
                continue;
            }
            if (historySequence != 0 && point.Sequence != historySequence + 1) {
                gapDiagnostics?.SequenceGap(historySequence, point.Sequence);
                BreakCollectedSeries(GapBreakReason.SequenceDiscontinuity);
            }
            historySequence = point.Sequence;
            var metric = point.Metric;
            if (metric.State == SampleState.Valid && (!double.IsFinite(metric.Value) || metric.Value < 0))
                metric = new Metric { State = SampleState.Error, Source = metric.Source };
            gapDiagnostics?.Point(point, metric, series.History.LastTime, point.TimeNs > series.History.LastTime);
            series.History.Append(point.TimeNs, metric);
        }
        BindCollectedSeries();
        return view.PointCount != 0 || view.LostPoints != 0 || view.CaptureErrors != 0;
    }

    private void BreakCollectedSeries(GapBreakReason reason)
    {
        gapDiagnostics?.Break(reason);
        foreach (var series in collectedSeries.Values) series.History.BreakNext();
    }

    private void BindCollectedSeries()
    {
        if (!collectedBindingsDirty) return;
        foreach (var series in collectedSeries.Values) {
            if (series.Channel == HistoryChannel.PageFile) { PageFile = series.History; continue; }
            if (series.Channel == HistoryChannel.Processor) {
                var key = (series.Group, series.Index);
                if (Processors.ContainsKey(key)) Processors[key] = series.History;
                continue;
            }
            if (!resources.TryGetValue(series.Resource, out var resource)) continue;
            switch (series.Channel) {
                case HistoryChannel.Primary: resource.AttachHistory(series.History, false); break;
                case HistoryChannel.Upload: resource.AttachHistory(series.History, true); break;
                case HistoryChannel.DiskRead: resource.AttachDiskHistory(series.History, false); break;
                case HistoryChannel.DiskWrite: resource.AttachDiskHistory(series.History, true); break;
                case HistoryChannel.Dedicated when resource.Gpu is { } dedicated: dedicated.Dedicated = series.History; break;
                case HistoryChannel.Shared when resource.Gpu is { } shared: shared.Shared = series.History; break;
                case HistoryChannel.Engine when resource.Gpu is { } engines:
                    if (engines.Engines.TryGetValue(series.Detail, out var engine)) engine.History = series.History;
                    break;
            }
        }
        collectedBindingsDirty = false;
    }
}
