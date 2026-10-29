#include "desktop.hpp"
#include "theme.hpp"
#include <algorithm>
#include <cwctype>

namespace nks {
using namespace theme;

void Desktop::processIcon(const std::wstring& name, float x, float y, float size)
{
    const std::array backgrounds{0xdcf2f6, 0xe1effe, 0xfcebd8, 0xe9edf2};
    const std::array foregrounds{0x167a9c, 0x176bc4, 0xad631d, 0x526273};
    unsigned hash = 0;
    for (auto c : name) hash = hash * 31 + c;
    const auto palette = hash % backgrounds.size();
    const auto initial = name.empty() ? L"?" : std::wstring(1, static_cast<wchar_t>(std::towlower(name.front())));
    fill(rect(x, y, size, size), D2D1::ColorF(backgrounds[palette]), 8);
    text(initial, rect(x, y + size * .19f, size, size * .7f), size * .4f,
        D2D1::ColorF(foregrounds[palette]), 700, 0, DWRITE_TEXT_ALIGNMENT_CENTER);
}

void Desktop::paintProcesses(float width, float height)
{
    const auto page = ProcessLayout::calculate(width, height);
    const auto card = [&](D2D1_RECT_F box, float radius) {
        fill(box, panel, radius);
        drawBrush->SetColor(line);
        surface->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(box.left + .5f, box.top + .5f, box.right - .5f, box.bottom - .5f), radius, radius), drawBrush, 1);
    };
    paintSearch(page.searchX, page.searchY, page.searchWidth, width > 1000);
    if (page.overview) {
        card(rect(page.left, page.overviewTop, page.width, page.overviewHeight), 10);
        if (!page.compact) {
            const auto captionWidth = (page.width - 146) / 5.3f * 1.3f;
            text(L"SYSTÉM V KOSTCE", rect(page.left + 23, page.overviewTop + 23, captionWidth, 16), 9, muted, 600, 1.6f);
            text(L"Každý proces v souvislostech.", rect(page.left + 23, page.overviewTop + 43, std::min(180.f, captionWidth), 49), width <= 1250 ? 13.f : 15.f, ink, 400);
        }
    }
    fill(rect(page.left, page.toolbarTop, 254, 37), D2D1::ColorF(0x12181c), 7);
    drawBrush->SetColor(line);
    surface->DrawRoundedRectangle(D2D1::RoundedRect(rect(page.left + .5f, page.toolbarTop + .5f, 253, 36), 7, 7), drawBrush, 1);
    if (!page.compact) text(std::to_wstring(table.count()) + (table.holding() ? L" položek · pořadí přidrženo pod kurzorem" : L" položek · podle " + table.sortDescription()),
        rect(page.left + 280, page.toolbarTop + 12, page.width - 280, 17), 9, muted, 400, 0, DWRITE_TEXT_ALIGNMENT_TRAILING);
    card(rect(page.left, page.tableTop, page.width, page.tableHeight + page.dockHeight), 10);
    fill(rect(page.left + 1, page.dockTop, page.width - 2, page.dockHeight - 10), panel);
    fill(rect(page.left, page.dockTop, page.width, 1), line);
    const auto* item = table.selected();
    if (!item) {
        text(table.selectionEnded() ? L"Vybraný proces již skončil" : L"Vyberte proces", rect(page.left + 21, page.dockTop + 20, page.width - 42, 24), 14, ink, 600);
        text(table.selectionEnded() ? L"Po opuštění seznamu se ukončený řádek odebere." : L"Ctrl a Shift vyberou více řádků · kliknutí na záhlaví změní řazení",
            rect(page.left + 21, page.dockTop + 49, page.width - 42, 20), 10, muted);
        return;
    }
    processIcon(item->name, page.left + 21, page.dockTop + 27, 38);
    const auto selectedCount = ListView_GetSelectedCount(table.handle());
    text(selectedCount > 1 ? std::to_wstring(selectedCount) + L" VYBRANÝCH · PRVNÍ PROCES" : L"VYBRANÝ PROCES",
        rect(page.left + 73, page.dockTop + 19, page.width - 450, 15), 9, muted, 600, 1.6f);
    text(item->name, rect(page.left + 73, page.dockTop + 38, page.width - 450, 23), 14, ink, 600);
    text(L"PID " + std::to_wstring(item->identity.pid) + L" · " + std::to_wstring(item->counters.threads) + L" vláken · "
        + std::to_wstring(item->counters.handles) + L" popisovačů", rect(page.left + 73, page.dockTop + 63, page.width - 450, 19), 9, muted);
    const auto statsX = page.left + page.width - 330;
    for (unsigned i = 0; i < 2; ++i) {
        const auto x = statsX + static_cast<float>(i) * 155;
        fill(rect(x, page.dockTop + 25, 1, 42), line);
        text(i == 0 ? L"CPU" : L"Pracovní sada", rect(x + 24, page.dockTop + 24, 128, 18), 9, muted);
        const auto value = processStale ? L"Zastaralé" : i == 0
            ? item->cpuPercent.valid() ? decimal(item->cpuPercent.value, L" %") : L"Nezměřeno"
            : decimal(item->counters.workingSetBytes / 1048576., L" MiB", 0);
        text(value, rect(x + 24, page.dockTop + 46, 128, 26), 17, ink, 600);
    }
}

void Desktop::drawOverview(const DRAWITEMSTRUCT& item)
{
    if (!beginControl(item, panel)) return;
    const auto w = controlSize.width, h = controlSize.height;
    const auto index = item.CtlID - 80;
    const std::array labels{L"CPU", L"Paměť", L"Disky", L"Síť"};
    const std::array kinds{ResourceKind::cpu, ResourceKind::memory, ResourceKind::disk, ResourceKind::network};
    const auto kind = kinds[index];
    const auto color = resourceColor(kind);
    text(labels[index], rect(0, 2, w, 17), 10, muted);
    std::wstring value = L"Nezměřeno", note;
    double amount = -1;
    if (index < 2) {
        const auto found = std::find_if(resources.begin(), resources.end(), [&](const auto& record) { return record.kind == kind; });
        if (found != resources.end()) {
            value = resourceValue(*found);
            if (found->primary.valid()) {
                amount = index == 0 ? found->primary.value : found->capacity > 0 ? found->primary.value / found->capacity * 100 : -1;
                if (index == 1) value = decimal(found->primary.value / 1073741824., L" / ") + decimal(found->capacity / 1073741824., L" GiB");
            }
        }
    } else {
        unsigned total = 0, measured = 0;
        double primary = 0, secondary = 0;
        for (const auto& record : resources) if (record.kind == kind) {
            ++total;
            const auto a = index == 2 ? record.secondary : record.primary;
            const auto b = index == 2 ? record.tertiary : record.secondary;
            if (a.valid() && b.valid()) { ++measured; primary += a.value; secondary += b.value; }
        }
        if (measured) value = index == 2 ? decimal((primary + secondary) / 1048576., L" MiB/s")
            : L"↓ " + decimal(primary / 1000000, L"") + L"  ↑ " + decimal(secondary / 1000000, L" Mbit/s");
        note = std::to_wstring(measured) + L" / " + std::to_wstring(total) + L" měřeno";
    }
    const auto size = w < 145 ? 13.f : w < 200 || index == 3 ? 17.f : 20.f;
    text(value, rect(0, 26, w, 28), size, ink, 600);
    if (amount >= 0) {
        fill(rect(0, h - 5, w, 3), line, 1);
        fill(rect(0, h - 5, w * static_cast<float>(std::clamp(amount / 100, 0., 1.)), 3), color);
    } else text(note, rect(0, h - 14, w, 15), 9, muted);
    if ((item.itemState & ODS_FOCUS) && focusFromKeyboard) {
        drawBrush->SetColor(amber);
        surface->DrawRoundedRectangle(D2D1::RoundedRect(rect(.5f, .5f, w - 1, h - 1), 4, 4), drawBrush, 1);
    }
    endControl();
}

LRESULT Desktop::drawProcessHeader(const NMCUSTOMDRAW& item)
{
    if (item.dwDrawStage == CDDS_PREPAINT) return CDRF_NOTIFYITEMDRAW | CDRF_NOTIFYPOSTPAINT;
    if (item.dwDrawStage == CDDS_POSTPAINT) {
        RECT gap{}; GetClientRect(table.header(), &gap);
        for (int i = 0; i < Header_GetItemCount(table.header()); ++i) {
            RECT column{}; Header_GetItemRect(table.header(), i, &column);
            gap.left = std::max(gap.left, column.right);
        }
        DRAWITEMSTRUCT entry{};
        entry.CtlType = ODT_HEADER; entry.hwndItem = table.header(); entry.hDC = item.hdc; entry.rcItem = gap;
        if (gap.left < gap.right && beginControl(entry, panel)) endControl();
        return CDRF_DODEFAULT;
    }
    if (item.dwDrawStage != CDDS_ITEMPREPAINT) return CDRF_DODEFAULT;
    const std::array labels{L"Proces / aplikace", L"PID", L"CPU", L"Pracovní sada", L"Soukromá paměť", L"Čtení I/O", L"Zápis I/O"};
    if (item.dwItemSpec >= labels.size()) return CDRF_DODEFAULT;
    DRAWITEMSTRUCT entry{};
    entry.CtlType = ODT_HEADER; entry.hwndItem = item.hdr.hwndFrom; entry.hDC = item.hdc; entry.rcItem = item.rc;
    if (!beginControl(entry, panel)) return CDRF_DODEFAULT;
    const auto w = controlSize.width, h = controlSize.height;
    auto label = std::wstring(labels[item.dwItemSpec]);
    if (static_cast<int>(item.dwItemSpec) == table.sortIndex()) label += table.sortDescending() ? L" ↓" : L" ↑";
    text(label, rect(item.dwItemSpec == 0 ? 19.f : 8.f, (h - 14) / 2, w - (item.dwItemSpec == 0 ? 32 : 21), 18),
        9, muted, 400, 0, item.dwItemSpec == 0 ? DWRITE_TEXT_ALIGNMENT_LEADING : DWRITE_TEXT_ALIGNMENT_TRAILING);
    fill(rect(0, h - 1, w, 1), line);
    endControl();
    return CDRF_SKIPDEFAULT;
}

LRESULT Desktop::drawProcessRow(const NMLVCUSTOMDRAW& item)
{
    if (item.nmcd.dwDrawStage == CDDS_PREPAINT) return CDRF_NOTIFYITEMDRAW | CDRF_NOTIFYPOSTPAINT;
    if (item.nmcd.dwDrawStage == CDDS_POSTPAINT) {
        RECT empty{}, header{}; GetClientRect(table.handle(), &empty); GetClientRect(table.header(), &header);
        empty.top = header.bottom;
        if (table.count()) {
            RECT last{}; ListView_GetItemRect(table.handle(), static_cast<int>(table.count() - 1), &last, LVIR_BOUNDS);
            empty.top = std::max(empty.top, last.bottom);
        }
        DRAWITEMSTRUCT entry{};
        entry.CtlType = ODT_LISTVIEW; entry.hwndItem = table.handle(); entry.hDC = item.nmcd.hdc; entry.rcItem = empty;
        if (empty.top < empty.bottom && beginControl(entry, panel)) {
            if (!table.count()) text(L"Tomuto hledání neodpovídá žádný proces.", rect(15, 35, controlSize.width - 30, 30), 12, muted, 400, 0, DWRITE_TEXT_ALIGNMENT_CENTER);
            endControl();
        }
        return CDRF_DODEFAULT;
    }
    if (item.nmcd.dwDrawStage != CDDS_ITEMPREPAINT) return CDRF_DODEFAULT;
    const auto row = static_cast<int>(item.nmcd.dwItemSpec);
    const auto* process = table.record(row);
    if (!process) return CDRF_DODEFAULT;
    RECT bounds{}; ListView_GetItemRect(table.handle(), row, &bounds, LVIR_BOUNDS);
    RECT client{}; GetClientRect(table.handle(), &client);
    bounds.right = std::max(bounds.right, client.right);
    DRAWITEMSTRUCT entry{};
    entry.CtlType = ODT_LISTVIEW; entry.hwndItem = table.handle(); entry.hDC = item.nmcd.hdc; entry.rcItem = bounds;
    const auto state = ListView_GetItemState(table.handle(), row, LVIS_SELECTED | LVIS_FOCUSED);
    const bool selected = (state & LVIS_SELECTED) != 0;
    const bool ended = table.ended(row);
    if (!beginControl(entry, selected ? D2D1::ColorF(0x28343b) : panel)) return CDRF_DODEFAULT;
    const auto h = controlSize.height, w = controlSize.width;
    if (selected) fill(rect(0, 0, 3, h), amber);
    fill(rect(0, h - 1, w, 1), line);
    for (int column = 0; column < 7; ++column) {
        RECT cell{}; Header_GetItemRect(table.header(), column, &cell);
        MapWindowPoints(table.header(), table.handle(), reinterpret_cast<POINT*>(&cell), 2);
        const auto x = (cell.left - bounds.left) / scale, cw = (cell.right - cell.left) / scale;
        if (column == 0) {
            const auto indent = table.mode() == ProcessMode::tree ? static_cast<float>(std::min(12U, table.depth(row))) * 14 : 0;
            if (!ended && table.mode() == ProcessMode::tree && table.hasChildren(row))
                text(table.isCollapsed(row) ? L"›" : L"⌄", rect(x + 13 + indent, 14, 20, 24), 15, muted, 400, 0, DWRITE_TEXT_ALIGNMENT_CENTER);
            processIcon(process->name, x + 40 + indent, 11, 29);
            text(process->name, rect(x + 80 + indent, 8, cw - 93 - indent, 20), 12, ended ? muted : ink, 600);
            text(L"PID " + std::to_wstring(process->identity.pid) + (ended ? L"  ·  ukončeno" : L"  ·  " + std::to_wstring(process->counters.threads) + L" vláken"),
                rect(x + 80 + indent, 30, cw - 93 - indent, 16), 9, muted);
            continue;
        }
        std::wstring value;
        if (column == 1) value = std::to_wstring(process->identity.pid);
        else if (ended) value = L"Ukončeno";
        else if (processStale) value = L"Zastaralé";
        else if (column == 2) value = process->cpuPercent.valid() ? decimal(process->cpuPercent.value, L" %") : L"—";
        else if (column == 3) value = decimal(process->counters.workingSetBytes / 1048576., L" MiB", 0);
        else if (column == 4) value = decimal(process->counters.privateBytes / 1048576., L" MiB", 0);
        else {
            const auto metric = column == 5 ? process->readIoBytesPerSecond : process->writeIoBytesPerSecond;
            value = metric.valid() ? decimal(metric.value / 1048576., L" MiB/s") : L"—";
        }
        text(value, rect(x + 8, 13, cw - 21, 21), 11, (processStale || ended) && column > 1 ? muted : ink, 400, 0, DWRITE_TEXT_ALIGNMENT_TRAILING);
        if ((column == 2 || column == 3) && !processStale && !ended) {
            const auto color = resourceColor(column == 2 ? ResourceKind::cpu : ResourceKind::memory);
            const auto amount = column == 2 ? process->cpuPercent.valid() ? process->cpuPercent.value / 100 : -1
                : processes && processes->memory.state == SampleState::valid && processes->memory.physicalBytes
                    ? static_cast<double>(process->counters.workingSetBytes) / processes->memory.physicalBytes : -1;
            const auto barWidth = std::min(70.f, cw - 26);
            fill(rect(x + cw - 13 - barWidth, 35, barWidth, 3), line, 1);
            if (amount >= 0) fill(rect(x + cw - 13 - barWidth, 35, barWidth * static_cast<float>(std::clamp(amount, 0., 1.)), 3), color);
        }
    }
    if ((state & LVIS_FOCUSED) && focusFromKeyboard && GetFocus() == table.handle()) {
        drawBrush->SetColor(amber);
        surface->DrawRectangle(rect(.5f, .5f, w - 1, h - 1), drawBrush, 1);
    }
    endControl();
    return CDRF_SKIPDEFAULT;
}

}
