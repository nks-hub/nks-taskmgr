#include "process_table.hpp"
#include <algorithm>
#include <iomanip>
#include <sstream>
#include <unordered_map>
#include <tuple>
#include <uxtheme.h>

namespace nks {
namespace {
bool contains(const std::wstring& text, const std::wstring& query)
{
    return FindStringOrdinal(FIND_FROMSTART, text.c_str(), static_cast<int>(text.size()), query.c_str(), static_cast<int>(query.size()), TRUE) >= 0;
}
std::wstring number(double value, const wchar_t* unit)
{
    std::wostringstream text;
    text << std::fixed << std::setprecision(1) << value << unit;
    return text.str();
}
}

void ProcessTable::create(HWND parent, HFONT font)
{
    window = CreateWindowExW(0, WC_LISTVIEWW, L"Procesy", WS_CHILD | WS_TABSTOP | LVS_REPORT | LVS_OWNERDATA
        | LVS_SHOWSELALWAYS | LVS_SHAREIMAGELISTS, 0, 0, 0, 0, parent, reinterpret_cast<HMENU>(101), nullptr, nullptr);
    ListView_SetExtendedListViewStyle(window, LVS_EX_FULLROWSELECT | LVS_EX_DOUBLEBUFFER | LVS_EX_HEADERDRAGDROP);
    SetWindowSubclass(window, procedure, 1, reinterpret_cast<DWORD_PTR>(this));
    SetWindowSubclass(header(), headerProcedure, 1, 0);
    SetWindowTheme(window, L"DarkMode_Explorer", nullptr);
    SetWindowTheme(header(), L"DarkMode_Explorer", nullptr);
    setFont(font);
    ListView_SetBkColor(window, RGB(29, 37, 42));
    ListView_SetTextBkColor(window, RGB(29, 37, 42));
    ListView_SetTextColor(window, RGB(231, 240, 244));
    const auto dpi = GetDpiForWindow(parent);
    const std::array names{L"Proces", L"PID", L"CPU", L"Pracovní sada", L"Soukromá paměť", L"Čtení I/O", L"Zápis I/O"};
    const std::array widths{270, 78, 90, 140, 155, 110, 110};
    for (int i = 0; i < static_cast<int>(names.size()); ++i) {
        LVCOLUMNW column{LVCF_TEXT | LVCF_WIDTH | LVCF_FMT};
        column.pszText = const_cast<wchar_t*>(names[i]);
        column.cx = MulDiv(widths[i], dpi, 96);
        column.fmt = i > 0 ? LVCFMT_RIGHT : LVCFMT_LEFT;
        ListView_InsertColumn(window, i, &column);
    }
}

void ProcessTable::setFont(HFONT font)
{
    const auto dpi = GetDpiForWindow(window);
    if (lastDpi && lastDpi != dpi)
        for (int i = 0; i < Header_GetItemCount(header()); ++i) ListView_SetColumnWidth(window, i, MulDiv(ListView_GetColumnWidth(window, i), dpi, lastDpi));
    lastDpi = dpi;
    SendMessageW(window, WM_SETFONT, reinterpret_cast<WPARAM>(font), FALSE);
    // ListView adds one physical pixel to the image height.
    auto next = ImageList_Create(1, MulDiv(34, GetDpiForWindow(window), 96) - 1, ILC_COLOR32, 1, 0);
    ListView_SetImageList(window, next, LVSIL_SMALL);
    if (images) ImageList_Destroy(images);
    images = next;
}

LRESULT CALLBACK ProcessTable::headerProcedure(HWND window, UINT message, WPARAM wp, LPARAM lp, UINT_PTR id, DWORD_PTR)
{
    if (message == HDM_LAYOUT) {
        const auto result = DefSubclassProc(window, message, wp, lp);
        auto& layout = *reinterpret_cast<HDLAYOUT*>(lp);
        const auto height = MulDiv(39, GetDpiForWindow(window), 96);
        layout.prc->top += height - layout.pwpos->cy;
        layout.pwpos->cy = height;
        return result;
    }
    if (message == WM_NCDESTROY) RemoveWindowSubclass(window, headerProcedure, id);
    return DefSubclassProc(window, message, wp, lp);
}

LRESULT CALLBACK ProcessTable::procedure(HWND window, UINT message, WPARAM wp, LPARAM lp, UINT_PTR id, DWORD_PTR)
{
    if (message == WM_NOTIFY) {
        const auto* notification = reinterpret_cast<NMHDR*>(lp);
        if (notification->hwndFrom == ListView_GetHeader(window) && notification->code == NM_CUSTOMDRAW)
            return SendMessageW(GetParent(window), WM_NOTIFY, wp, lp);
    }
    if (message == WM_NCDESTROY) RemoveWindowSubclass(window, procedure, id);
    return DefSubclassProc(window, message, wp, lp);
}

void ProcessTable::resizeColumns()
{
    const auto dpi = GetDpiForWindow(window);
    RECT client{}; GetClientRect(window, &client);
    if (client.right == sizedClientWidth) return;
    sizedClientWidth = client.right;
    int remaining = 0;
    for (int i = 1; i < Header_GetItemCount(header()); ++i) remaining += ListView_GetColumnWidth(window, i);
    const auto next = std::max(MulDiv(270, dpi, 96), static_cast<int>(client.right) - remaining);
    if (ListView_GetColumnWidth(window, 0) != next) ListView_SetColumnWidth(window, 0, next);
}

void ProcessTable::setMode(ProcessMode value)
{
    setHeld(false);
    displayMode = value;
    update(snapshot);
}

void ProcessTable::setHeld(bool value)
{
    if (held == value) return;
    if (value) {
        heldRows.reserve(rows.size());
        for (auto index : rows) heldRows.push_back(snapshot->processes[index]);
        endedRows.assign(rows.size(), false);
        held = true;
    } else {
        held = false;
        update(snapshot);
    }
}

void ProcessTable::setVisible(bool value)
{
    if (!value && (GetWindowLongPtrW(window, GWL_STYLE) & WS_VISIBLE)) {
        topAnchorIndex = ListView_GetTopIndex(window);
        topAnchor.reset();
        if (topAnchorIndex > 0) if (const auto* item = record(topAnchorIndex)) topAnchor = item->identity;
    }
    ShowWindow(window, value ? SW_SHOWNA : SW_HIDE);
    if (value) restoreScroll();
}

void ProcessTable::restoreScroll()
{
    if (!topAnchor || !(GetWindowLongPtrW(window, GWL_STYLE) & WS_VISIBLE)) return;
    auto index = std::min(topAnchorIndex, std::max(0, static_cast<int>(rows.size()) - 1));
    for (std::size_t i = 0; i < rows.size(); ++i)
        if (const auto* item = record(i); item && item->identity == *topAnchor) { index = static_cast<int>(i); break; }
    RECT bounds{};
    if (ListView_GetItemRect(window, index, &bounds, LVIR_BOUNDS))
        ListView_Scroll(window, 0, (index - ListView_GetTopIndex(window)) * (bounds.bottom - bounds.top));
    topAnchor.reset();
}

void ProcessTable::update(std::shared_ptr<const ProcessSnapshot> value)
{
    if (held) {
        snapshot = std::move(value);
        std::unordered_map<std::uint32_t, const ProcessRecord*> current;
        if (snapshot) for (const auto& process : snapshot->processes) current.emplace(process.identity.pid, &process);
        for (std::size_t i = 0; i < heldRows.size(); ++i) {
            const auto found = current.find(heldRows[i].identity.pid);
            endedRows[i] = found == current.end() || found->second->identity != heldRows[i].identity;
            if (!endedRows[i]) heldRows[i] = *found->second;
        }
        InvalidateRect(window, nullptr, FALSE);
        return;
    }
    std::vector<ProcessIdentity> selectedIds;
    for (int i = ListView_GetNextItem(window, -1, LVNI_SELECTED); i >= 0; i = ListView_GetNextItem(window, i, LVNI_SELECTED))
        if (const auto* item = record(i); item && !ended(i)) selectedIds.push_back(item->identity);
    std::optional<ProcessIdentity> focusId;
    const int top = ListView_GetTopIndex(window);
    const int focus = ListView_GetNextItem(window, -1, LVNI_FOCUSED);
    if (!topAnchor && top > 0) if (const auto* item = record(top)) { topAnchor = item->identity; topAnchorIndex = top; }
    if (focus >= 0) if (const auto* item = record(focus)) focusId = item->identity;
    heldRows.clear(); endedRows.clear();
    snapshot = std::move(value);
    rebuild();
    resizeColumns();
    ListView_SetItemState(window, -1, 0, LVIS_SELECTED | LVIS_FOCUSED);
    for (std::size_t i = 0; i < rows.size(); ++i) {
        const auto& id = snapshot->processes[rows[i]].identity;
        if (std::find(selectedIds.begin(), selectedIds.end(), id) != selectedIds.end())
            ListView_SetItemState(window, static_cast<int>(i), LVIS_SELECTED, LVIS_SELECTED);
        if (focusId && id == *focusId)
            ListView_SetItemState(window, static_cast<int>(i), LVIS_FOCUSED, LVIS_FOCUSED);
    }
    restoreScroll();
    InvalidateRect(window, nullptr, FALSE);
}

void ProcessTable::filter(std::wstring text) { setHeld(false); search = std::move(text); update(snapshot); }

void ProcessTable::rebuild()
{
    rows.clear();
    depths.clear();
    branches.clear();
    if (!snapshot) return;
    for (std::size_t i = 0; i < snapshot->processes.size(); ++i) {
        const auto& process = snapshot->processes[i];
        const bool active = (process.cpuPercent.valid() && process.cpuPercent.value > 0)
            || (process.readIoBytesPerSecond.valid() && process.readIoBytesPerSecond.value > 0)
            || (process.writeIoBytesPerSecond.valid() && process.writeIoBytesPerSecond.value > 0);
        if ((displayMode != ProcessMode::active || active) && (search.empty() || contains(process.name, search)
            || std::to_wstring(process.identity.pid).find(search) != std::wstring::npos)) rows.push_back(i);
    }
    const auto value = [&](const ProcessRecord& p) {
        switch (sortColumn) {
        case 1: return static_cast<double>(p.identity.pid);
        case 2: return p.cpuPercent.valid() ? p.cpuPercent.value : -1;
        case 3: return static_cast<double>(p.counters.workingSetBytes);
        case 4: return static_cast<double>(p.counters.privateBytes);
        case 5: return p.readIoBytesPerSecond.valid() ? p.readIoBytesPerSecond.value : -1;
        default: return p.writeIoBytesPerSecond.valid() ? p.writeIoBytesPerSecond.value : -1;
        }
    };
    std::sort(rows.begin(), rows.end(), [&](std::size_t a, std::size_t b) {
        const auto& left = snapshot->processes[a];
        const auto& right = snapshot->processes[b];
        const auto order = sortColumn == 0 ? CompareStringOrdinal(left.name.c_str(), static_cast<int>(left.name.size()),
            right.name.c_str(), static_cast<int>(right.name.size()), TRUE) - CSTR_EQUAL
            : value(left) < value(right) ? -1 : value(left) > value(right) ? 1 : 0;
        if (order == 0) return left.identity.pid < right.identity.pid;
        return descending ? order > 0 : order < 0;
    });
    if (displayMode == ProcessMode::tree) buildTree();
    else { depths.resize(rows.size()); branches.resize(rows.size()); }
    ListView_SetItemCountEx(window, static_cast<int>(rows.size()), LVSICF_NOSCROLL | LVSICF_NOINVALIDATEALL);
}

void ProcessTable::buildTree()
{
    const auto sorted = rows;
    std::unordered_map<std::uint32_t, std::size_t> byPid;
    for (auto index : sorted) byPid.emplace(snapshot->processes[index].identity.pid, index);
    std::vector<std::vector<std::size_t>> children(snapshot->processes.size());
    std::vector<std::size_t> roots;
    for (auto index : sorted) {
        const auto& child = snapshot->processes[index];
        const auto parent = byPid.find(child.parentPid);
        bool attached = false;
        if (parent != byPid.end() && parent->second != index) {
            const auto& ancestor = snapshot->processes[parent->second];
            if (!ancestor.identity.boot.empty() && ancestor.identity.boot == child.identity.boot
                && ancestor.created100ns && child.created100ns && ancestor.created100ns <= child.created100ns) {
                children[parent->second].push_back(index); attached = true;
            }
        }
        if (!attached) roots.push_back(index);
    }
    std::erase_if(collapsed, [&](const auto& identity) {
        const auto found = byPid.find(identity.pid);
        return found == byPid.end() || snapshot->processes[found->second].identity != identity;
    });
    rows.clear();
    std::vector<bool> visited(snapshot->processes.size());
    const auto walk = [&](std::size_t root) {
        std::vector<std::tuple<std::size_t, unsigned, bool>> pending{{root, 0, true}};
        while (!pending.empty()) {
            const auto [index, depth, shown] = pending.back(); pending.pop_back();
            if (visited[index]) continue;
            visited[index] = true;
            if (shown) { rows.push_back(index); depths.push_back(depth); branches.push_back(!children[index].empty()); }
            const auto& identity = snapshot->processes[index].identity;
            const auto showChildren = shown && std::find(collapsed.begin(), collapsed.end(), identity) == collapsed.end();
            for (auto child = children[index].rbegin(); child != children[index].rend(); ++child) pending.emplace_back(*child, depth + 1, showChildren);
        }
    };
    for (auto root : roots) walk(root);
    for (auto index : sorted) if (!visited[index]) walk(index);
}

const ProcessRecord* ProcessTable::record(std::size_t row) const
{
    if (!heldRows.empty()) return row < heldRows.size() ? &heldRows[row] : nullptr;
    return snapshot && row < rows.size() ? &snapshot->processes[rows[row]] : nullptr;
}

bool ProcessTable::isCollapsed(std::size_t row) const
{
    const auto* item = record(row);
    return item && std::find(collapsed.begin(), collapsed.end(), item->identity) != collapsed.end();
}

void ProcessTable::toggle(std::size_t row)
{
    const auto* item = record(row);
    if (displayMode != ProcessMode::tree || !item || !hasChildren(row)) return;
    const auto identity = item->identity;
    if (ended(row)) return;
    setHeld(false);
    if (!snapshot || std::none_of(snapshot->processes.begin(), snapshot->processes.end(), [&](const auto& process) { return process.identity == identity; })) return;
    const auto found = std::find(collapsed.begin(), collapsed.end(), identity);
    if (found == collapsed.end()) collapsed.push_back(identity); else collapsed.erase(found);
    update(snapshot);
}

std::wstring ProcessTable::sortDescription() const
{
    const std::array labels{L"názvu", L"PID", L"CPU", L"pracovní sady", L"soukromé paměti", L"čtení I/O", L"zápisu I/O"};
    return labels[sortColumn];
}

std::wstring ProcessTable::cell(std::size_t row, int column) const
{
    const auto* item = record(row);
    if (!item) return {};
    if (ended(row) && column > 1) return L"Ukončeno";
    if (stale && column > 1) return L"Zastaralé";
    const auto& process = *item;
    switch (column) {
    case 0: return process.name;
    case 1: return std::to_wstring(process.identity.pid);
    case 2: return process.cpuPercent.valid() ? number(process.cpuPercent.value, L" %") : L"—";
    case 3: return number(static_cast<double>(process.counters.workingSetBytes) / 1048576, L" MiB");
    case 4: return number(static_cast<double>(process.counters.privateBytes) / 1048576, L" MiB");
    case 5: return process.readIoBytesPerSecond.valid() ? number(process.readIoBytesPerSecond.value / 1048576, L" MiB/s") : L"—";
    case 6: return process.writeIoBytesPerSecond.valid() ? number(process.writeIoBytesPerSecond.value / 1048576, L" MiB/s") : L"—";
    default: return {};
    }
}

LRESULT ProcessTable::notify(NMHDR* notification)
{
    if (notification->code == LVN_GETEMPTYMARKUP) {
        auto& empty = *reinterpret_cast<NMLVEMPTYMARKUP*>(notification);
        empty.dwFlags = EMF_CENTERED;
        wcscpy_s(empty.szMarkup, L"Tomuto hledání neodpovídá žádný proces.");
        return TRUE;
    } else if (notification->code == LVN_GETDISPINFOW) {
        auto& item = reinterpret_cast<NMLVDISPINFOW*>(notification)->item;
        if (item.mask & LVIF_TEXT) {
            const auto text = cell(static_cast<std::size_t>(item.iItem), item.iSubItem);
            wcsncpy_s(item.pszText, item.cchTextMax, text.c_str(), _TRUNCATE);
        }
    } else if (notification->code == LVN_COLUMNCLICK) {
        setHeld(false);
        const auto column = reinterpret_cast<NMLISTVIEW*>(notification)->iSubItem;
        descending = column == sortColumn ? !descending : column > 0;
        sortColumn = column;
        update(snapshot);
    } else if (notification->code == NM_CLICK && displayMode == ProcessMode::tree) {
        const auto* click = reinterpret_cast<NMITEMACTIVATE*>(notification);
        const auto indent = std::min(12U, depth(static_cast<std::size_t>(click->iItem)));
        RECT name{};
        Header_GetItemRect(header(), 0, &name);
        MapWindowPoints(header(), window, reinterpret_cast<POINT*>(&name), 2);
        const auto left = name.left + MulDiv(3 + static_cast<int>(indent) * 14, GetDpiForWindow(window), 96);
        if (click->iItem >= 0 && click->iSubItem == 0 && click->ptAction.x >= left && click->ptAction.x <= left + MulDiv(18, GetDpiForWindow(window), 96))
            toggle(static_cast<std::size_t>(click->iItem));
    } else if (notification->code == LVN_KEYDOWN && displayMode == ProcessMode::tree) {
        const auto key = reinterpret_cast<NMLVKEYDOWN*>(notification)->wVKey;
        const auto row = ListView_GetNextItem(window, -1, LVNI_FOCUSED);
        if (row >= 0 && ((key == VK_LEFT && !isCollapsed(row)) || (key == VK_RIGHT && isCollapsed(row)))) toggle(row);
    }
    return 0;
}

const ProcessRecord* ProcessTable::selected() const
{
    const auto row = ListView_GetNextItem(window, -1, LVNI_SELECTED);
    return row < 0 || ended(row) ? nullptr : record(row);
}

bool ProcessTable::selectionEnded() const
{
    const auto row = ListView_GetNextItem(window, -1, LVNI_SELECTED);
    return row >= 0 && ended(row);
}

void ProcessTable::select(const ProcessIdentity& identity)
{
    if (!snapshot) return;
    for (std::size_t i = 0; i < rows.size(); ++i)
        if (const auto* item = record(i); item && !ended(i) && item->identity == identity) {
            ListView_SetItemState(window, -1, 0, LVIS_SELECTED | LVIS_FOCUSED);
            ListView_SetItemState(window, static_cast<int>(i), LVIS_SELECTED | LVIS_FOCUSED, LVIS_SELECTED | LVIS_FOCUSED);
            ListView_EnsureVisible(window, static_cast<int>(i), FALSE);
            break;
        }
}

}
