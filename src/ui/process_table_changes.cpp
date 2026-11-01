#include "process_table.hpp"
#include <algorithm>
#include <iterator>
#include <unordered_set>

namespace nks {
namespace {
bool contains(const std::wstring& text, const std::wstring& query)
{
    return FindStringOrdinal(FIND_FROMSTART, text.c_str(), static_cast<int>(text.size()), query.c_str(), static_cast<int>(query.size()), TRUE) >= 0;
}
bool presentationChanged(const ProcessRecord& before, const ProcessRecord& after)
{
    const auto metric = [](const Metric<double>& left, const Metric<double>& right) {
        return left.valid() != right.valid() || (left.valid() && left.value != right.value);
    };
    return before.name != after.name || before.counters.workingSetBytes != after.counters.workingSetBytes
        || before.counters.privateBytes != after.counters.privateBytes || metric(before.cpuPercent, after.cpuPercent)
        || metric(before.readIoBytesPerSecond, after.readIoBytesPerSecond) || metric(before.writeIoBytesPerSecond, after.writeIoBytesPerSecond);
}
}

bool ProcessTable::matches(const ProcessRecord& process) const
{
    const bool active = (process.cpuPercent.valid() && process.cpuPercent.value > 0)
        || (process.readIoBytesPerSecond.valid() && process.readIoBytesPerSecond.value > 0)
        || (process.writeIoBytesPerSecond.valid() && process.writeIoBytesPerSecond.value > 0);
    return (displayMode != ProcessMode::active || active) && (search.empty() || contains(process.name, search)
        || std::to_wstring(process.identity.pid).find(search) != std::wstring::npos);
}

bool ProcessTable::less(const ProcessRecord& left, const ProcessRecord& right) const
{
    const auto value = [&](const ProcessRecord& process) {
        switch (sortColumn) {
        case 1: return static_cast<double>(process.identity.pid);
        case 2: return process.cpuPercent.valid() ? process.cpuPercent.value : -1;
        case 3: return static_cast<double>(process.counters.workingSetBytes);
        case 4: return static_cast<double>(process.counters.privateBytes);
        case 5: return process.readIoBytesPerSecond.valid() ? process.readIoBytesPerSecond.value : -1;
        default: return process.writeIoBytesPerSecond.valid() ? process.writeIoBytesPerSecond.value : -1;
        }
    };
    const auto order = sortColumn == 0 ? CompareStringOrdinal(left.name.c_str(), static_cast<int>(left.name.size()),
        right.name.c_str(), static_cast<int>(right.name.size()), TRUE) - CSTR_EQUAL
        : value(left) < value(right) ? -1 : value(left) > value(right) ? 1 : 0;
    if (order == 0) return left.identity.pid < right.identity.pid;
    return descending ? order > 0 : order < 0;
}

void ProcessTable::indexSnapshot()
{
    snapshotIndices.clear();
    if (!snapshot) return;
    snapshotIndices.reserve(snapshot->processes.size());
    for (std::size_t i = 0; i < snapshot->processes.size(); ++i) snapshotIndices.emplace(snapshot->processes[i].identity.pid, i);
}

ProcessTable::ViewState ProcessTable::captureView() const
{
    ViewState state;
    for (int row = ListView_GetNextItem(window, -1, LVNI_SELECTED); row >= 0; row = ListView_GetNextItem(window, row, LVNI_SELECTED))
        if (const auto* process = record(row); process && !ended(row)) state.selected.push_back(process->identity);
    const auto key = [&](int row) -> std::optional<ProcessIdentity> {
        if (row >= 0) if (const auto* process = record(row); process && !ended(row)) return process->identity;
        return {};
    };
    state.focus = key(ListView_GetNextItem(window, -1, LVNI_FOCUSED));
    state.mark = key(ListView_GetSelectionMark(window));
    state.topIndex = topAnchor ? topAnchorIndex : ListView_GetTopIndex(window);
    state.top = topAnchor;
    if (!state.top) if (const auto* process = record(state.topIndex)) state.top = process->identity;
    return state;
}

void ProcessTable::restoreView(const ViewState& state)
{
    ListView_SetItemState(window, -1, 0, LVIS_SELECTED | LVIS_FOCUSED);
    int mark = -1;
    for (std::size_t i = 0; i < rows.size(); ++i) {
        const auto* process = record(i); if (!process || ended(i)) continue;
        const auto& key = process->identity;
        if (std::find(state.selected.begin(), state.selected.end(), key) != state.selected.end())
            ListView_SetItemState(window, static_cast<int>(i), LVIS_SELECTED, LVIS_SELECTED);
        if (state.focus && key == *state.focus)
            ListView_SetItemState(window, static_cast<int>(i), LVIS_FOCUSED, LVIS_FOCUSED);
        if (state.mark && key == *state.mark) mark = static_cast<int>(i);
    }
    ListView_SetSelectionMark(window, mark);
    topAnchor = state.top; topAnchorIndex = state.topIndex;
    restoreScroll();
}

void ProcessTable::redrawRows(const std::vector<bool>& dirty)
{
    std::size_t first = 0;
    while (first < dirty.size()) {
        if (!dirty[first]) { ++first; continue; }
        auto last = first;
        while (last + 1 < dirty.size() && dirty[last + 1]) ++last;
        ListView_RedrawItems(window, static_cast<int>(first), static_cast<int>(last)); first = last + 1;
    }
}

void ProcessTable::applyHeldChanges(std::shared_ptr<const ProcessSnapshot> value, const ProcessChanges& changes)
{
    snapshot = std::move(value);
    std::vector<bool> dirty(heldRows.size());
    for (const auto& change : changes.entries) {
        const auto found = heldIndices.find(change.pid);
        if (found == heldIndices.end()) continue;
        const auto row = found->second;
        auto& process = heldRows[row];
        if (change.kind == ProcessChangeKind::removed) {
            if (process.identity.generationKind == change.generationKind && process.identity.generation == change.generation && !endedRows[row]) {
                endedRows[row] = true; dirty[row] = true;
            }
        } else {
            const auto& current = snapshot->processes[change.index];
            if (current.identity == process.identity) {
                dirty[row] = endedRows[row] || presentationChanged(process, current);
                process = current; endedRows[row] = false;
            }
        }
    }
    redrawRows(dirty);
}

void ProcessTable::update(std::shared_ptr<const ProcessSnapshot> value, const ProcessChanges& changes)
{
    if (!snapshot || !value || changes.reset) { update(std::move(value)); return; }
    if (held) { applyHeldChanges(std::move(value), changes); return; }
    std::unordered_map<std::uint32_t, std::size_t> nextIndices;
    nextIndices.reserve(value->processes.size());
    for (std::size_t i = 0; i < value->processes.size(); ++i) nextIndices.emplace(value->processes[i].identity.pid, i);
    std::unordered_set<std::uint32_t> included, reorder, touched;
    included.reserve(orderedRows.size()); touched.reserve(changes.entries.size());
    for (auto index : orderedRows) included.insert(snapshot->processes[index].identity.pid);
    bool treeChanged = false;
    for (const auto& change : changes.entries) {
        const auto before = snapshotIndices.find(change.pid), after = nextIndices.find(change.pid);
        if (before != snapshotIndices.end() && after != nextIndices.end()
            && presentationChanged(snapshot->processes[before->second], value->processes[after->second])) touched.insert(change.pid);
        const bool wasIncluded = included.contains(change.pid);
        const bool nowIncluded = after != nextIndices.end() && matches(value->processes[after->second]);
        if (change.kind != ProcessChangeKind::updated || wasIncluded != nowIncluded || before == snapshotIndices.end()) {
            if (wasIncluded || nowIncluded) reorder.insert(change.pid);
        } else if (wasIncluded) {
            const auto& oldRow = snapshot->processes[before->second]; const auto& newRow = value->processes[after->second];
            if (less(oldRow, newRow) || less(newRow, oldRow)) reorder.insert(change.pid);
            if (displayMode == ProcessMode::tree && (oldRow.parentPid != newRow.parentPid || oldRow.created100ns != newRow.created100ns)) treeChanged = true;
        }
    }
    std::vector<std::size_t> stable, moved, nextOrder;
    stable.reserve(orderedRows.size()); moved.reserve(reorder.size());
    for (auto index : orderedRows) {
        const auto& old = snapshot->processes[index]; const auto current = nextIndices.find(old.identity.pid);
        if (reorder.contains(old.identity.pid)) continue;
        // The underlying snapshot array may move unchanged rows without emitting row changes.
        if (current == nextIndices.end() || value->processes[current->second].identity != old.identity) { update(std::move(value)); return; }
        stable.push_back(current->second);
    }
    for (auto pid : reorder) if (const auto found = nextIndices.find(pid); found != nextIndices.end() && matches(value->processes[found->second])) moved.push_back(found->second);
    const auto compare = [&](std::size_t a, std::size_t b) { return less(value->processes[a], value->processes[b]); };
    std::sort(moved.begin(), moved.end(), compare);
    nextOrder.reserve(stable.size() + moved.size());
    std::merge(stable.begin(), stable.end(), moved.begin(), moved.end(), std::back_inserter(nextOrder), compare);
    treeChanged = treeChanged || !reorder.empty();
    const auto state = treeChanged ? captureView() : ViewState{};
    const auto previous = snapshot; const auto previousRows = rows; const auto previousDepths = depths; const auto previousBranches = branches;
    std::vector<std::size_t> nextRows;
    if (displayMode == ProcessMode::tree && !treeChanged) {
        nextRows.reserve(rows.size());
        for (auto index : rows) nextRows.push_back(nextIndices.at(snapshot->processes[index].identity.pid));
    }
    snapshot = std::move(value); snapshotIndices = std::move(nextIndices); orderedRows = std::move(nextOrder);
    if (displayMode == ProcessMode::tree) {
        if (treeChanged) { rows = orderedRows; depths.clear(); branches.clear(); buildTree(); }
        else rows = std::move(nextRows);
    } else { rows = orderedRows; depths.assign(rows.size(), 0); branches.assign(rows.size(), false); }
    bool positionsChanged = previousRows.size() != rows.size();
    std::vector<bool> dirty(rows.size());
    for (std::size_t i = 0; i < rows.size(); ++i) {
        const auto& current = snapshot->processes[rows[i]];
        const bool position = i >= previousRows.size() || previous->processes[previousRows[i]].identity != current.identity;
        positionsChanged = positionsChanged || position;
        dirty[i] = position || touched.contains(current.identity.pid) || i >= previousDepths.size()
            || previousDepths[i] != depths[i] || previousBranches[i] != branches[i];
    }
    RECT removed{}; const bool erasedTail = rows.size() < previousRows.size()
        && ListView_GetItemRect(window, static_cast<int>(rows.size()), &removed, LVIR_BOUNDS);
    if (ListView_GetItemCount(window) != static_cast<int>(rows.size()))
        ListView_SetItemCountEx(window, static_cast<int>(rows.size()), LVSICF_NOSCROLL | LVSICF_NOINVALIDATEALL);
    if (positionsChanged) restoreView(state);
    if (erasedTail) { RECT client{}; GetClientRect(window, &client); removed.left = 0; removed.right = client.right; removed.bottom = client.bottom; InvalidateRect(window, &removed, FALSE); }
    redrawRows(dirty);
}
}
