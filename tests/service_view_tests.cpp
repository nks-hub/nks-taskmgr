#include "ui/service_table.hpp"
#include <iostream>
#include <stdexcept>

#pragma comment(linker, "\"/manifestdependency:type='win32' name='Microsoft.Windows.Common-Controls' version='6.0.0.0' processorArchitecture='*' publicKeyToken='6595b64144ccf1df' language='*'\"")

namespace {
void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }

void verify(nks::ServiceTable& table)
{
    auto snapshot = std::make_shared<nks::ServiceSnapshot>();
    for (unsigned i = 0; i < 80; ++i) {
        nks::ServiceRecord record;
        record.name = L"Svc" + std::to_wstring(100 + i); record.title = L"Služba " + std::to_wstring(100 + i);
        record.state = i % 2 ? SERVICE_RUNNING : SERVICE_STOPPED; record.pid = i % 2 ? i + 100 : 0;
        snapshot->services.push_back(record);
    }
    table.update(snapshot); table.setVisible(true); table.resizeColumns();
    table.select(L"svc115");
    require(table.selected() && table.selected()->name == L"Svc115", "Case-insensitive SCM selection failed");
    RECT row{}; ListView_GetItemRect(table.handle(), 0, &row, LVIR_BOUNDS);
    require(row.bottom - row.top == 57, "Service row height differs from the design");
    auto next = std::make_shared<nks::ServiceSnapshot>(*snapshot);
    next->services[15].state = SERVICE_PAUSED;
    table.update(next);
    require(table.selected() && table.selected()->state == SERVICE_PAUSED, "Refresh lost the selected service identity or live status");
    table.filter(nks::ServiceFilter::other, L"SLUŽBA");
    require(table.count() == 1 && table.selected() && table.selected()->name == L"Svc115", "Unicode service filter lost the paused selection");
    table.filter(nks::ServiceFilter::stopped, L"");
    require(table.count() == 40 && !table.selected(), "Stopped filter retained a paused service");
    table.filter(nks::ServiceFilter::all, L"115");
    require(table.count() == 1, "Service name/PID filter failed");
    table.filter(nks::ServiceFilter::all, L"");
    table.select(L"Svc115");
    NMLISTVIEW sorting{}; sorting.hdr.code = LVN_COLUMNCLICK; sorting.iSubItem = 3;
    table.notify(&sorting.hdr);
    require(table.selected() && table.selected()->name == L"Svc115", "Column sort changed the service target");
    table.filter(nks::ServiceFilter::all, L"");
    ListView_EnsureVisible(table.handle(), 60, FALSE);
    const auto before = ListView_GetTopIndex(table.handle());
    require(before > 0 && table.record(before), "Service scroll setup failed");
    const auto anchor = table.record(before)->name;
    table.setVisible(false);
    auto inserted = std::make_shared<nks::ServiceSnapshot>(*next);
    nks::ServiceRecord extra; extra.name = L"Svc000"; extra.title = L"Služba 000"; extra.state = SERVICE_STOPPED;
    inserted->services.insert(inserted->services.begin(), extra);
    table.update(inserted); table.update(inserted); table.setVisible(true);
    require(table.record(ListView_GetTopIndex(table.handle()))->name == anchor, "Hidden service refresh lost the scroll anchor");
    auto removed = std::make_shared<nks::ServiceSnapshot>(*next);
    std::erase_if(removed->services, [](const auto& record) { return record.name == L"Svc115"; });
    table.update(removed);
    require(!table.selected(), "A deleted service retained a different live selection");
}
}

int main()
{
    INITCOMMONCONTROLSEX controls{sizeof(controls), ICC_LISTVIEW_CLASSES}; InitCommonControlsEx(&controls);
    const auto parent = CreateWindowExW(0, L"STATIC", L"Service table test", WS_OVERLAPPEDWINDOW,
        0, 0, 850, 500, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!parent) return 2;
    int result = 0;
    {
        nks::ServiceTable table;
        table.create(parent, static_cast<HFONT>(GetStockObject(DEFAULT_GUI_FONT)));
        MoveWindow(table.handle(), 0, 0, 800, 400, FALSE);
        try { verify(table); std::cout << "Service filters, identity, live status, row height, sort and hidden scroll passed\n"; }
        catch (const std::exception& error) { std::cerr << error.what() << '\n'; result = 1; }
        DestroyWindow(parent);
    }
    return result;
}
