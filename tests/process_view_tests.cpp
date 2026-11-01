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
    value.identity = {{1, 2}, pid, nks::GenerationKind::creationTime, created};
    value.created100ns = created;
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
    check(rowBounds.bottom - rowBounds.top == MulDiv(34, GetDpiForWindow(table.handle()), 96), "Native row height differs from the UI layout");
    table.select(snapshot->processes[0].identity);
    table.setHeld(true);
    auto moving = std::make_shared<nks::ProcessSnapshot>(*snapshot);
    moving->processes[0].cpuPercent.value = 7;
    moving->processes[1].identity.generation = 30;
    moving->processes[1].created100ns = 30;
    moving->processes.push_back(process(L"D-new.exe", 500, 0, 50));
    table.update(moving);
    check(table.holding() && table.count() == 3, "A new process shifted held rows");
    check(table.record(0)->cpuPercent.value == 7 && table.selected()->identity == snapshot->processes[0].identity, "Held rows stopped updating live counters");
    check(table.ended(1) && table.record(1)->identity.generation == 10, "A reused PID replaced a held row identity");
    auto removed = std::make_shared<nks::ProcessSnapshot>(*moving);
    removed->processes.erase(removed->processes.begin());
    table.update(removed);
    check(table.count() == 3 && table.ended(0), "A terminated process shifted held rows");
    check(!table.selected() && table.selectionEnded(), "An ended row remained a live target");
    table.setHeld(false);
    check(table.count() == 3 && !table.holding() && !table.selected(), "Releasing held rows restored an ended selection");
    check(table.record(0)->identity.pid == 100 && table.record(0)->identity.generation == 30, "Releasing held rows did not apply the current process generation");
    table.update(snapshot);
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
    click.ptAction.x = nameColumn.left + MulDiv(12, GetDpiForWindow(table.handle()), 96);
    table.notify(&click.hdr);
    check(table.count() == 2, "Reordered process column broke the collapse hit target");
    table.notify(&click.hdr);
    check(table.count() == 3, "Reordered process column broke the expand hit target");
    click.ptAction.x = nameColumn.left + MulDiv(34, GetDpiForWindow(table.handle()), 96);
    table.notify(&click.hdr);
    check(table.count() == 3, "Clicking the process icon toggled the branch");
    table.select(snapshot->processes[1].identity);
    auto reused = std::make_shared<nks::ProcessSnapshot>(*snapshot);
    reused->processes[1].identity.generation = 30;
    reused->processes[1].created100ns = 30;
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
    table.setMode(nks::ProcessMode::all);
    table.setHeld(true);
    auto busier = std::make_shared<nks::ProcessSnapshot>(*snapshot);
    busier->processes[1].cpuPercent.value = 70;
    table.update(busier);
    check(table.record(0)->identity.pid == 400, "Live CPU sorting moved the row under the pointer");
    table.setHeld(false);
    check(table.record(0)->identity.pid == 100, "CPU sorting did not resume after leaving the rows");
    auto cycle = std::make_shared<nks::ProcessSnapshot>();
    cycle->processes = {process(L"A.exe", 100, 200, 10), process(L"B.exe", 200, 100, 10)};
    table.setMode(nks::ProcessMode::tree);
    table.update(cycle);
    check(table.count() == 2, "Cycle lost or duplicated a process");
    table.toggle(0);
    check(table.count() == 1, "Cycle escaped a collapsed branch");
    table.setMode(nks::ProcessMode::all);
    MoveWindow(table.handle(), 0, 0, 900, 260, FALSE);
    table.setVisible(true);
    auto many = std::make_shared<nks::ProcessSnapshot>();
    for (unsigned i = 0; i < 30; ++i) many->processes.push_back(process((L"Process-" + std::to_wstring(i)).c_str(), 1000 + i, 0, 10 + i, i));
    table.update(many);
    ListView_EnsureVisible(table.handle(), 12, FALSE);
    const auto top = ListView_GetTopIndex(table.handle());
    check(top > 0, "Scroll test did not reach a lower row");
    const auto topIdentity = table.record(top)->identity;
    auto reordered = std::make_shared<nks::ProcessSnapshot>(*many);
    reordered->processes[0].cpuPercent.value = 100;
    table.update(reordered);
    check(table.record(ListView_GetTopIndex(table.handle()))->identity == topIdentity, "Refresh lost the scrolled top process");
    ListView_EnsureVisible(table.handle(), 0, FALSE);
    check(ListView_GetTopIndex(table.handle()) == 0, "Scroll test did not return to the top");
    reordered = std::make_shared<nks::ProcessSnapshot>(*reordered);
    reordered->processes[1].cpuPercent.value = 200;
    table.update(reordered);
    check(ListView_GetTopIndex(table.handle()) == 0 && table.record(0)->identity.pid == 1001, "Live sorting scrolled away from the highest CPU process");
    ListView_EnsureVisible(table.handle(), 12, FALSE);
    const auto hiddenTop = table.record(ListView_GetTopIndex(table.handle()))->identity;
    table.setVisible(false);
    auto hidden = std::make_shared<nks::ProcessSnapshot>(*reordered);
    hidden->processes[2].cpuPercent.value = 180;
    table.update(hidden);
    hidden = std::make_shared<nks::ProcessSnapshot>(*hidden);
    hidden->processes[3].cpuPercent.value = 190;
    table.update(hidden);
    table.setVisible(true);
    check(table.record(ListView_GetTopIndex(table.handle()))->identity == hiddenTop, "Updates in another view lost the saved scroll identity");
}

void verifySequenceTree(nks::ProcessTable& table)
{
    auto snapshot = std::make_shared<nks::ProcessSnapshot>();
    snapshot->processes = {process(L"A-child.exe", 200, 100, 20), process(L"B-parent.exe", 100, 0, 10)};
    snapshot->processes[0].identity.generationKind = nks::GenerationKind::sequenceNumber;
    snapshot->processes[0].identity.generation = 100;
    snapshot->processes[1].identity.generationKind = nks::GenerationKind::sequenceNumber;
    snapshot->processes[1].identity.generation = 900;
    table.setMode(nks::ProcessMode::tree);
    table.update(snapshot);
    check(table.count() == 2 && table.record(0)->identity == snapshot->processes[1].identity && table.depth(0) == 0,
        "Sequence identity was interpreted as the parent creation time");
    check(table.record(1)->identity == snapshot->processes[0].identity && table.depth(1) == 1 && table.hasChildren(0),
        "Sequence child did not attach using the independent raw creation times");
    table.select(snapshot->processes[0].identity);

    auto newer = std::make_shared<nks::ProcessSnapshot>(*snapshot);
    newer->processes[1].identity.generation = 1;
    newer->processes[1].created100ns = 30;
    table.update(newer);
    check(table.count() == 2 && table.depth(0) == 0 && table.depth(1) == 0,
        "A newer raw parent creation time was accepted because its sequence value was lower");
    check(table.selected() && table.selected()->identity == snapshot->processes[0].identity,
        "A changed sequence parent replaced the selected full child identity");

    auto missingTime = std::make_shared<nks::ProcessSnapshot>(*snapshot);
    missingTime->processes[1].created100ns = 0;
    table.update(missingTime);
    check(table.depth(0) == 0 && table.depth(1) == 0, "A valid sequence identity substituted for a missing raw creation time");
    auto otherBoot = std::make_shared<nks::ProcessSnapshot>(*snapshot);
    otherBoot->processes[1].identity.boot = {3, 4};
    table.update(otherBoot);
    check(table.depth(0) == 0 && table.depth(1) == 0, "A tree parent from another verified boot was accepted");
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
        verifySequenceTree(table);
        DestroyWindow(parent);
        std::cout << checks << " process-view checks passed\n";
        return 0;
    } catch (const std::exception& error) {
        if (parent) DestroyWindow(parent);
        std::cerr << error.what() << '\n';
        return 1;
    }
}
