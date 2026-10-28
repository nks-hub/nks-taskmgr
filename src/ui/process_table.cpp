#include "process_table.hpp"
#include <algorithm>
#include <cwctype>
#include <iomanip>
#include <sstream>

namespace nks {
namespace {
std::wstring lower(std::wstring text)
{
    std::transform(text.begin(), text.end(), text.begin(), [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
    return text;
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
    setFont(font);
    ListView_SetBkColor(window, RGB(23, 31, 36));
    ListView_SetTextBkColor(window, RGB(23, 31, 36));
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
    SendMessageW(window, WM_SETFONT, reinterpret_cast<WPARAM>(font), FALSE);
    auto next = ImageList_Create(1, MulDiv(34, GetDpiForWindow(window), 96), ILC_COLOR32, 1, 0);
    ListView_SetImageList(window, next, LVSIL_SMALL);
    if (images) ImageList_Destroy(images);
    images = next;
}

void ProcessTable::update(std::shared_ptr<const ProcessSnapshot> value)
{
    std::vector<ProcessIdentity> selectedIds;
    for (int i = ListView_GetNextItem(window, -1, LVNI_SELECTED); i >= 0; i = ListView_GetNextItem(window, i, LVNI_SELECTED))
        if (snapshot && static_cast<std::size_t>(i) < rows.size()) selectedIds.push_back(snapshot->processes[rows[i]].identity);
    std::optional<ProcessIdentity> topId, focusId;
    const int top = ListView_GetTopIndex(window);
    const int focus = ListView_GetNextItem(window, -1, LVNI_FOCUSED);
    if (snapshot && top >= 0 && static_cast<std::size_t>(top) < rows.size()) topId = snapshot->processes[rows[top]].identity;
    if (snapshot && focus >= 0 && static_cast<std::size_t>(focus) < rows.size()) focusId = snapshot->processes[rows[focus]].identity;
    snapshot = std::move(value);
    rebuild();
    ListView_SetItemState(window, -1, 0, LVIS_SELECTED | LVIS_FOCUSED);
    for (std::size_t i = 0; i < rows.size(); ++i) {
        const auto& id = snapshot->processes[rows[i]].identity;
        if (std::find(selectedIds.begin(), selectedIds.end(), id) != selectedIds.end())
            ListView_SetItemState(window, static_cast<int>(i), LVIS_SELECTED, LVIS_SELECTED);
        if (focusId && id == *focusId)
            ListView_SetItemState(window, static_cast<int>(i), LVIS_FOCUSED, LVIS_FOCUSED);
        if (topId && id == *topId) {
            const auto currentTop = ListView_GetTopIndex(window);
            RECT rect{};
            if (ListView_GetItemRect(window, static_cast<int>(i), &rect, LVIR_BOUNDS))
                ListView_Scroll(window, 0, (static_cast<int>(i) - currentTop) * (rect.bottom - rect.top));
        }
    }
    InvalidateRect(window, nullptr, FALSE);
}

void ProcessTable::filter(std::wstring text) { search = lower(std::move(text)); update(snapshot); }

void ProcessTable::rebuild()
{
    rows.clear();
    if (!snapshot) return;
    for (std::size_t i = 0; i < snapshot->processes.size(); ++i) {
        const auto& process = snapshot->processes[i];
        if (search.empty() || lower(process.name).find(search) != std::wstring::npos
            || std::to_wstring(process.identity.pid).find(search) != std::wstring::npos) rows.push_back(i);
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
    ListView_SetItemCountEx(window, static_cast<int>(rows.size()), LVSICF_NOSCROLL | LVSICF_NOINVALIDATEALL);
}

std::wstring ProcessTable::cell(std::size_t row, int column) const
{
    if (!snapshot || row >= rows.size()) return {};
    if (stale && column > 1) return L"Zastaralé";
    const auto& process = snapshot->processes[rows[row]];
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
    if (notification->code == LVN_GETDISPINFOW) {
        auto& item = reinterpret_cast<NMLVDISPINFOW*>(notification)->item;
        if (item.mask & LVIF_TEXT) {
            const auto text = cell(static_cast<std::size_t>(item.iItem), item.iSubItem);
            wcsncpy_s(item.pszText, item.cchTextMax, text.c_str(), _TRUNCATE);
        }
    } else if (notification->code == LVN_COLUMNCLICK) {
        const auto column = reinterpret_cast<NMLISTVIEW*>(notification)->iSubItem;
        descending = column == sortColumn ? !descending : column > 0;
        sortColumn = column;
        update(snapshot);
    } else if (notification->code == NM_CUSTOMDRAW) {
        auto* draw = reinterpret_cast<NMLVCUSTOMDRAW*>(notification);
        if (draw->nmcd.dwDrawStage == CDDS_PREPAINT) return CDRF_NOTIFYITEMDRAW;
        if (draw->nmcd.dwDrawStage == CDDS_ITEMPREPAINT) {
            const bool selectedRow = ListView_GetItemState(window, static_cast<int>(draw->nmcd.dwItemSpec), LVIS_SELECTED) != 0;
            draw->clrTextBk = selectedRow ? RGB(45, 59, 66) : RGB(23, 31, 36);
            draw->clrText = RGB(231, 240, 244);
        }
    }
    return 0;
}

const ProcessRecord* ProcessTable::selected() const
{
    const auto row = ListView_GetNextItem(window, -1, LVNI_SELECTED);
    return !snapshot || row < 0 || static_cast<std::size_t>(row) >= rows.size() ? nullptr : &snapshot->processes[rows[row]];
}

void ProcessTable::select(const ProcessIdentity& identity)
{
    if (!snapshot) return;
    for (std::size_t i = 0; i < rows.size(); ++i)
        if (snapshot->processes[rows[i]].identity == identity) {
            ListView_SetItemState(window, -1, 0, LVIS_SELECTED | LVIS_FOCUSED);
            ListView_SetItemState(window, static_cast<int>(i), LVIS_SELECTED | LVIS_FOCUSED, LVIS_SELECTED | LVIS_FOCUSED);
            ListView_EnsureVisible(window, static_cast<int>(i), FALSE);
            break;
        }
}

}
