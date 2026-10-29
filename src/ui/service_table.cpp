#include "service_table.hpp"
#include <algorithm>
#include <uxtheme.h>

namespace nks {

void ServiceTable::create(HWND parent, HFONT font)
{
    window = CreateWindowExW(0, WC_LISTVIEWW, L"Služby systému", WS_CHILD | WS_TABSTOP | LVS_REPORT | LVS_OWNERDATA
        | LVS_SINGLESEL | LVS_SHOWSELALWAYS | LVS_SHAREIMAGELISTS, 0, 0, 0, 0, parent, reinterpret_cast<HMENU>(102), nullptr, nullptr);
    ListView_SetExtendedListViewStyle(window, LVS_EX_FULLROWSELECT | LVS_EX_DOUBLEBUFFER);
    SetWindowSubclass(window, procedure, 1, 0);
    SetWindowSubclass(header(), headerProcedure, 1, 0);
    SetWindowTheme(window, L"DarkMode_Explorer", nullptr);
    SetWindowTheme(header(), L"DarkMode_Explorer", nullptr);
    ListView_SetBkColor(window, RGB(29, 37, 42));
    ListView_SetTextBkColor(window, RGB(29, 37, 42));
    ListView_SetTextColor(window, RGB(233, 238, 240));
    const std::array labels{L"Služba", L"Stav", L"Spouštění", L"PID"};
    for (int i = 0; i < 4; ++i) {
        LVCOLUMNW column{LVCF_TEXT | LVCF_WIDTH | LVCF_FMT};
        column.pszText = const_cast<wchar_t*>(labels[i]); column.cx = 100;
        column.fmt = i == 3 ? LVCFMT_RIGHT : LVCFMT_LEFT;
        ListView_InsertColumn(window, i, &column);
    }
    setFont(font);
}

void ServiceTable::setFont(HFONT font)
{
    SendMessageW(window, WM_SETFONT, reinterpret_cast<WPARAM>(font), FALSE);
    auto next = ImageList_Create(1, MulDiv(57, GetDpiForWindow(window), 96) - 1, ILC_COLOR32, 1, 0);
    ListView_SetImageList(window, next, LVSIL_SMALL);
    if (images) ImageList_Destroy(images);
    images = next; sizedWidth = 0;
}

void ServiceTable::resizeColumns()
{
    RECT client{}; GetClientRect(window, &client);
    if (client.right == sizedWidth) return;
    sizedWidth = client.right;
    const auto width = std::max(static_cast<int>(client.right), MulDiv(510, GetDpiForWindow(window), 96));
    const std::array ratios{.41, .18, .30, .11};
    int previous = 0; double total = 0;
    for (int i = 0; i < 4; ++i) {
        total += ratios[i]; const auto end = static_cast<int>(width * total);
        ListView_SetColumnWidth(window, i, end - previous); previous = end;
    }
}

LRESULT CALLBACK ServiceTable::headerProcedure(HWND window, UINT message, WPARAM wp, LPARAM lp, UINT_PTR id, DWORD_PTR)
{
    if (message == HDM_LAYOUT) {
        const auto result = DefSubclassProc(window, message, wp, lp);
        auto& layout = *reinterpret_cast<HDLAYOUT*>(lp);
        const auto height = MulDiv(39, GetDpiForWindow(window), 96);
        layout.prc->top += height - layout.pwpos->cy; layout.pwpos->cy = height;
        return result;
    }
    if (message == WM_NCDESTROY) RemoveWindowSubclass(window, headerProcedure, id);
    return DefSubclassProc(window, message, wp, lp);
}

LRESULT CALLBACK ServiceTable::procedure(HWND window, UINT message, WPARAM wp, LPARAM lp, UINT_PTR id, DWORD_PTR)
{
    if (message == WM_NOTIFY) {
        const auto* notification = reinterpret_cast<NMHDR*>(lp);
        if (notification->hwndFrom == ListView_GetHeader(window) && notification->code == NM_CUSTOMDRAW)
            return SendMessageW(GetParent(window), WM_NOTIFY, wp, lp);
    }
    if (message == WM_NCDESTROY) RemoveWindowSubclass(window, procedure, id);
    return DefSubclassProc(window, message, wp, lp);
}

const ServiceRecord* ServiceTable::record(std::size_t row) const
{
    return snapshot && row < rows.size() ? &snapshot->services[rows[row]] : nullptr;
}

const ServiceRecord* ServiceTable::selected() const { return record(ListView_GetNextItem(window, -1, LVNI_SELECTED)); }

void ServiceTable::update(std::shared_ptr<const ServiceSnapshot> value)
{
    const auto selectedName = selected() ? selected()->name : std::wstring{};
    if ((GetWindowLongPtrW(window, GWL_STYLE) & WS_VISIBLE) != 0) {
        const auto top = ListView_GetTopIndex(window);
        anchor = top > 0 && record(top) ? record(top)->name : std::wstring{};
    }
    rebuilding = true;
    snapshot = std::move(value); rebuild(); select(selectedName); restoreScroll();
    rebuilding = false;
}

void ServiceTable::filter(ServiceFilter value, std::wstring text)
{
    const auto name = selected() ? selected()->name : std::wstring{};
    rebuilding = true;
    mode = value; query = std::move(text); anchor.clear(); rebuild(); select(name);
    rebuilding = false;
}

void ServiceTable::rebuild()
{
    rows.clear();
    if (snapshot) for (std::size_t i = 0; i < snapshot->services.size(); ++i)
        if (serviceMatches(snapshot->services[i], mode, query)) rows.push_back(i);
    const auto compareText = [](const std::wstring& a, const std::wstring& b) {
        return CompareStringOrdinal(a.data(), static_cast<int>(a.size()), b.data(), static_cast<int>(b.size()), TRUE) - CSTR_EQUAL;
    };
    std::sort(rows.begin(), rows.end(), [&](auto first, auto second) {
        const auto& a = snapshot->services[first]; const auto& b = snapshot->services[second];
        int order = 0;
        if (sortColumn == 0) order = compareText(a.title, b.title);
        else if (sortColumn == 1) order = static_cast<int>(a.state) - static_cast<int>(b.state);
        else if (sortColumn == 2) order = compareText(serviceStartup(a.config), serviceStartup(b.config));
        else order = a.pid < b.pid ? -1 : a.pid > b.pid ? 1 : 0;
        if (!order) order = compareText(a.name, b.name);
        return descending ? order > 0 : order < 0;
    });
    ListView_SetItemState(window, -1, 0, LVIS_SELECTED | LVIS_FOCUSED);
    ListView_SetItemCountEx(window, static_cast<int>(rows.size()), LVSICF_NOSCROLL);
    resizeColumns();
    InvalidateRect(window, nullptr, FALSE);
}

void ServiceTable::select(const std::wstring& name)
{
    if (name.empty()) return;
    for (std::size_t i = 0; i < rows.size(); ++i) if (serviceNameEqual(record(i)->name, name)) {
        ListView_SetItemState(window, static_cast<int>(i), LVIS_SELECTED | LVIS_FOCUSED, LVIS_SELECTED | LVIS_FOCUSED);
        return;
    }
}

void ServiceTable::restoreScroll()
{
    if (anchor.empty() || (GetWindowLongPtrW(window, GWL_STYLE) & WS_VISIBLE) == 0) return;
    for (std::size_t i = 0; i < rows.size(); ++i) if (record(i)->name == anchor) {
        RECT row{}; ListView_GetItemRect(window, static_cast<int>(i), &row, LVIR_BOUNDS);
        const auto distance = (static_cast<int>(i) - ListView_GetTopIndex(window)) * (row.bottom - row.top);
        ListView_Scroll(window, 0, distance); return;
    }
}

void ServiceTable::setVisible(bool value)
{
    if (!value && (GetWindowLongPtrW(window, GWL_STYLE) & WS_VISIBLE) != 0) {
        const auto top = ListView_GetTopIndex(window);
        anchor = top > 0 && record(top) ? record(top)->name : std::wstring{};
    }
    ShowWindow(window, value ? SW_SHOW : SW_HIDE);
    if (value) restoreScroll();
}

std::wstring ServiceTable::cell(std::size_t row, int column) const
{
    const auto* value = record(row); if (!value) return {};
    switch (column) {
    case 0: return value->title + L", " + value->name;
    case 1: return serviceStatus(value->state);
    case 2: return serviceStartup(value->config);
    case 3: return value->pid ? std::to_wstring(value->pid) : L"—";
    default: return {};
    }
}

LRESULT ServiceTable::notify(NMHDR* notification)
{
    if (notification->code == LVN_GETDISPINFOW) {
        auto& item = reinterpret_cast<NMLVDISPINFOW*>(notification)->item;
        if (item.mask & LVIF_TEXT) lstrcpynW(item.pszText, cell(item.iItem, item.iSubItem).c_str(), item.cchTextMax);
    } else if (notification->code == LVN_COLUMNCLICK) {
        const auto column = reinterpret_cast<NMLISTVIEW*>(notification)->iSubItem;
        descending = column == sortColumn ? !descending : false; sortColumn = column;
        update(snapshot); InvalidateRect(header(), nullptr, FALSE);
    }
    return 0;
}

}
