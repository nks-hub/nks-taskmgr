#include "ui/process_table.hpp"
#include <iostream>
#include <stdexcept>

#pragma comment(linker, "\"/manifestdependency:type='win32' name='Microsoft.Windows.Common-Controls' version='6.0.0.0' processorArchitecture='*' publicKeyToken='6595b64144ccf1df' language='*'\"")

namespace {
unsigned checks{};
void check(bool condition, const char* message)
{
    if (!condition) throw std::runtime_error(message);
    ++checks;
}

nks::ProcessRecord process(const wchar_t* name, std::uint32_t pid, std::uint32_t parent, std::uint64_t created, double cpu = 0)
{
    nks::ProcessRecord value;
    value.name = name;
    value.identity = {1, pid, nks::GenerationKind::creationTime, created};
    value.parentPid = parent;
    value.cpuPercent = {cpu, nks::SampleState::valid};
    return value;
}

void verify(nks::ProcessTable& table)
{
    auto snapshot = std::make_shared<nks::ProcessSnapshot>();
    snapshot->processes = {process(L"A-child.exe", 200, 100, 20), process(L"B-parent.exe", 100, 0, 10), process(L"ČÍTAČ.exe", 400, 0, 15, 2)};
    table.update(snapshot);
    check(table.count() == 3, "Flat view lost a process");
    RECT rowBounds{}; ListView_GetItemRect(table.handle(), 0, &rowBounds, LVIR_BOUNDS);
    check(rowBounds.bottom - rowBounds.top == MulDiv(52, GetDpiForWindow(table.handle()), 96), "Native row height differs from the UI layout");
    table.setMode(nks::ProcessMode::tree);
    check(table.record(0)->identity.pid == 100 && table.depth(0) == 0, "Parent must precede its child");
    check(table.record(1)->identity.pid == 200 && table.depth(1) == 1, "Child depth is missing");
    check(table.hasChildren(0) && !table.hasChildren(1), "Wrong tree branches");
    const auto child = snapshot->processes[0].identity;
    table.select(child);
    table.update(std::make_shared<nks::ProcessSnapshot>(*snapshot));
    check(table.selected() && table.selected()->identity == child, "Refresh changed the selected identity");
    table.toggle(0);
    check(table.count() == 2 && table.isCollapsed(0), "Collapsed descendants reappeared as roots");
    table.toggle(0);
    check(table.count() == 3 && table.depth(1) == 1, "Expanded child was not restored");
    int order[]{1, 0, 2, 3, 4, 5, 6};
    ListView_SetColumnOrderArray(table.handle(), 7, order);
    RECT nameColumn{}; Header_GetItemRect(table.header(), 0, &nameColumn);
    MapWindowPoints(table.header(), table.handle(), reinterpret_cast<POINT*>(&nameColumn), 2);
    NMITEMACTIVATE click{};
    click.hdr.code = NM_CLICK; click.iItem = 0; click.iSubItem = 0;
    click.ptAction.x = nameColumn.left + MulDiv(23, GetDpiForWindow(table.handle()), 96);
    table.notify(&click.hdr);
    check(table.count() == 2, "Reordered process column broke the collapse hit target");
    table.notify(&click.hdr);
    check(table.count() == 3, "Reordered process column broke the expand hit target");
    table.select(snapshot->processes[1].identity);
    auto reused = std::make_shared<nks::ProcessSnapshot>(*snapshot);
    reused->processes[1].identity.generation = 30;
    table.update(reused);
    check(!table.selected(), "Selection followed a reused PID");
    check(table.depth(0) == 0 && table.depth(1) == 0 && table.depth(2) == 0, "Child attached to a newer reused parent PID");
    reused->processes[1].identity.generationKind = nks::GenerationKind::missing;
    table.update(reused);
    check(table.depth(0) == 0 && table.depth(1) == 0, "Tree accepted an unverified parent generation");
    table.filter(L"čítač");
    check(table.count() == 1 && table.record(0)->identity.pid == 400, "Unicode case-insensitive search failed");
    table.filter(L"200");
    check(table.count() == 1 && table.record(0)->identity == child, "PID search failed");
    table.filter(L"missing-process");
    check(table.count() == 0 && !table.selected(), "Empty filter retained an invisible selection");
    table.filter(L"");
    snapshot->processes[1].readIoBytesPerSecond = {64, nks::SampleState::valid};
    snapshot->processes[0].cpuPercent = {5, nks::SampleState::unavailable};
    table.setMode(nks::ProcessMode::active);
    table.update(snapshot);
    check(table.count() == 2, "Activity filter used an invalid CPU sample or missed I/O");
    NMLISTVIEW sort{};
    sort.hdr.code = LVN_COLUMNCLICK; sort.iSubItem = 2;
    table.notify(&sort.hdr);
    check(table.record(0)->identity.pid == 400, "CPU sorting did not reorder active processes");
    auto cycle = std::make_shared<nks::ProcessSnapshot>();
    cycle->processes = {process(L"A.exe", 100, 200, 10), process(L"B.exe", 200, 100, 10)};
    table.setMode(nks::ProcessMode::tree);
    table.update(cycle);
    check(table.count() == 2, "Cycle lost or duplicated a process");
    table.toggle(0);
    check(table.count() == 1, "Cycle escaped a collapsed branch");
}
}

int main()
{
    HWND parent{};
    try {
        INITCOMMONCONTROLSEX controls{sizeof(controls), ICC_LISTVIEW_CLASSES};
        if (!InitCommonControlsEx(&controls)) throw std::runtime_error("Common controls unavailable");
        parent = CreateWindowExW(0, L"STATIC", L"Process view tests", WS_OVERLAPPED, 0, 0, 900, 600, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
        if (!parent) throw std::runtime_error("Test window unavailable");
        nks::ProcessTable table;
        table.create(parent, static_cast<HFONT>(GetStockObject(DEFAULT_GUI_FONT)));
        verify(table);
        DestroyWindow(parent);
        std::cout << checks << " process-view checks passed\n";
        return 0;
    } catch (const std::exception& error) {
        if (parent) DestroyWindow(parent);
        std::cerr << error.what() << '\n';
        return 1;
    }
}
