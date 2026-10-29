#include "desktop.hpp"
#include "theme.hpp"
#include <algorithm>

namespace nks {
using namespace theme;

void Desktop::paintSearch(float x, float y, float width, bool shortcut)
{
    fill(rect(x, y, width, 35), D2D1::ColorF(0x12181c), 7);
    drawBrush->SetColor(line);
    surface->DrawRoundedRectangle(D2D1::RoundedRect(rect(x + .5f, y + .5f, width - 1, 34), 7, 7), drawBrush, 1);
    drawBrush->SetColor(muted);
    surface->DrawEllipse(D2D1::Ellipse(D2D1::Point2F(x + 18, y + 16), 4, 4), drawBrush, 1);
    surface->DrawLine(D2D1::Point2F(x + 21, y + 19), D2D1::Point2F(x + 25, y + 23), drawBrush, 1);
    if (shortcut) text(L"Ctrl F", rect(x + width - 38, y + 12, 30, 17), 9, muted);
}

void Desktop::paintServices(float width, float height)
{
    const auto frame = ServiceLayout::calculate(width, height);
    paintSearch(frame.searchX, frame.searchY, frame.searchWidth, width > 1000);
    const auto card = [&](D2D1_RECT_F box) {
        fill(box, panel, 10); drawBrush->SetColor(line);
        surface->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(box.left + .5f, box.top + .5f, box.right - .5f, box.bottom - .5f), 10, 10), drawBrush, 1);
    };
    if (frame.overview) {
        card(rect(frame.left, frame.overviewTop, frame.width, frame.overviewHeight));
        const auto captionWidth = frame.width - std::min(176.f, frame.width / 4) * 4 - 46;
        if (captionWidth >= 200) {
            text(L"SPRÁVA SYSTÉMU", rect(frame.left + 23, frame.overviewTop + 24, captionWidth, 16), 9, muted, 600, 1.6f);
            text(L"Služba, její proces i závislosti.", rect(frame.left + 23, frame.overviewTop + 44, captionWidth, 28), 16, ink, 400);
        }
    }
    card(rect(frame.left, frame.workspaceTop, frame.width, frame.bottom - frame.workspaceTop));
    const auto detailTop = frame.stacked ? frame.tableTop + frame.tableHeight : frame.workspaceTop;
    fill(rect(frame.detailX + 1, detailTop + 1, frame.detailWidth - 2, frame.bottom - detailTop - 2), D2D1::ColorF(0x171d21));
    fill(frame.stacked ? rect(frame.detailX, detailTop, frame.detailWidth, 1)
        : rect(frame.detailX, detailTop, 1, frame.bottom - detailTop), line);
    fill(rect(frame.left, frame.tableTop - 1, frame.inventoryWidth, 1), line);
    fill(rect(frame.left + 19, frame.workspaceTop + 18, 309, 37), D2D1::ColorF(0x12181c), 6);
    if (frame.inventoryWidth > 510) text(std::to_wstring(serviceTable.count()) + L" služeb",
        rect(frame.left + frame.inventoryWidth - 125, frame.workspaceTop + 26, 105, 17), 10, muted, 400, 0, DWRITE_TEXT_ALIGNMENT_TRAILING);
    const auto* selected = serviceTable.selected();
    if (!selected) {
        text(L"Vyberte službu", rect(frame.detailX + 23, detailTop + 23, frame.detailWidth - 46, 40), 22, ink, 500, -.3f);
        return;
    }
    if (!frame.stacked && !frame.compactDetail) {
        fill(rect(frame.detailX + 23, detailTop + 21, 43, 43), panel, 11);
        drawBrush->SetColor(line);
        surface->DrawRoundedRectangle(D2D1::RoundedRect(rect(frame.detailX + 23.5f, detailTop + 21.5f, 42, 42), 11, 11), drawBrush, 1);
        icon(6, frame.detailX + 34, detailTop + 32, 21, amber);
        text(serviceStatus(selected->state), rect(frame.detailX + 85, detailTop + 36, frame.detailWidth - 108, 19), 10,
            selected->state == SERVICE_RUNNING ? D2D1::ColorF(0x83bca0) : muted, 400, 0, DWRITE_TEXT_ALIGNMENT_TRAILING);
    }
    text(selected->title, rect(frame.detailX + 23, detailTop + (frame.stacked || frame.compactDetail ? 12 : 81),
        frame.detailWidth - (frame.stacked ? 204 : 46), frame.stacked ? 27.f : frame.compactDetail ? 38.f : 60.f),
        frame.stacked || frame.compactDetail ? 16.f : 22.f, ink, 500, -.3f);
    text(selected->name, rect(frame.detailX + 23, detailTop + (frame.stacked ? 42 : frame.compactDetail ? 56 : 148), frame.detailWidth - 46, 18), 10, muted);
}

void Desktop::drawServiceCounter(const DRAWITEMSTRUCT& item)
{
    if (!beginControl(item, panel)) return;
    const auto index = static_cast<int>(item.CtlID) - 120;
    std::size_t count = 0;
    if (services) count = static_cast<std::size_t>(std::count_if(services->services.begin(), services->services.end(), [&](const auto& record) {
        return serviceMatches(record, static_cast<ServiceFilter>(index), L"");
    }));
    const std::array labels{L"Celkem", L"Běží", L"Zastavené", L"Ostatní"};
    const auto w = controlSize.width, h = controlSize.height;
    fill(rect(0, 5, 1, h - 10), line);
    const auto active = serviceTable.filterMode() == static_cast<ServiceFilter>(index);
    const auto vertical = w < 160;
    const auto number = text(services ? std::to_wstring(count) : L"—", rect(vertical ? 17.f : 28.f, vertical ? 0.f : 8.f, w - 30, 35), 28, active ? amber : ink, 500);
    const auto labelX = vertical ? 19.f : 28 + number.width + 9;
    text(labels[index], rect(labelX, vertical ? 38.f : 28.f, w - labelX - 12, 17), 10, muted);
    if ((item.itemState & ODS_FOCUS) && focusFromKeyboard) {
        drawBrush->SetColor(amber); surface->DrawRectangle(rect(3, 2, w - 6, h - 4), drawBrush, 1);
    }
    endControl();
}

void Desktop::drawDependency(const DRAWITEMSTRUCT& item)
{
    if (item.itemID >= dependencyRows.size() || !beginControl(item, D2D1::ColorF(0x171d21))) return;
    const auto& [name, dependent] = dependencyRows[item.itemID];
    const auto w = controlSize.width, h = controlSize.height;
    const auto known = services && std::any_of(services->services.begin(), services->services.end(), [&](const auto& record) { return serviceNameEqual(record.name, name); });
    fill(rect(.5f, 1, w - 1, h - 5), (item.itemState & ODS_SELECTED) ? D2D1::ColorF(0x28343b) : panel, 6);
    icon(6, 10, 12, 17, known ? amber : muted);
    text(name, rect(37, 7, w - 62, 18), 11, ink);
    text(dependent ? L"Navazující služba" : name.starts_with(L"+") ? L"Skupina služeb" : L"Potřebuje ke spuštění", rect(37, 25, w - 50, 13), 9, muted);
    if (known) icon(5, w - 21, 13, 13, muted);
    if ((item.itemState & ODS_FOCUS) && focusFromKeyboard) {
        drawBrush->SetColor(amber); surface->DrawRectangle(rect(1, 1, w - 2, h - 3), drawBrush, 1);
    }
    endControl();
}

LRESULT Desktop::drawServiceHeader(const NMCUSTOMDRAW& item)
{
    if (item.dwDrawStage == CDDS_PREPAINT) return CDRF_NOTIFYITEMDRAW;
    if (item.dwDrawStage != CDDS_ITEMPREPAINT || item.dwItemSpec >= 4) return CDRF_DODEFAULT;
    DRAWITEMSTRUCT entry{}; entry.hwndItem = serviceTable.header(); entry.hDC = item.hdc; entry.rcItem = item.rc;
    if (!beginControl(entry, panel)) return CDRF_DODEFAULT;
    const std::array labels{L"Služba", L"Stav", L"Spouštění", L"PID"};
    auto label = std::wstring(labels[item.dwItemSpec]);
    if (static_cast<int>(item.dwItemSpec) == serviceTable.sortIndex()) label += serviceTable.sortDescending() ? L" ↓" : L" ↑";
    const auto w = controlSize.width, h = controlSize.height;
    text(label, rect(18, (h - 14) / 2, w - 36, 18), 10, muted, 400, 0,
        item.dwItemSpec == 3 ? DWRITE_TEXT_ALIGNMENT_TRAILING : DWRITE_TEXT_ALIGNMENT_LEADING);
    fill(rect(0, h - 1, w, 1), line);
    endControl(); return CDRF_SKIPDEFAULT;
}

LRESULT Desktop::drawServiceRow(const NMLVCUSTOMDRAW& item)
{
    if (item.nmcd.dwDrawStage == CDDS_PREPAINT) return CDRF_NOTIFYITEMDRAW | CDRF_NOTIFYPOSTPAINT;
    if (item.nmcd.dwDrawStage == CDDS_POSTPAINT) {
        RECT empty{}, header{}; GetClientRect(serviceTable.handle(), &empty); GetClientRect(serviceTable.header(), &header);
        empty.top = header.bottom;
        if (serviceTable.count()) {
            RECT last{}; ListView_GetItemRect(serviceTable.handle(), static_cast<int>(serviceTable.count() - 1), &last, LVIR_BOUNDS);
            empty.top = std::max(empty.top, last.bottom);
        }
        DRAWITEMSTRUCT entry{}; entry.CtlType = ODT_LISTVIEW; entry.hwndItem = serviceTable.handle(); entry.hDC = item.nmcd.hdc; entry.rcItem = empty;
        if (empty.top < empty.bottom && beginControl(entry, panel)) {
            if (!serviceTable.count()) text(services ? L"Žádná služba neodpovídá filtru." : L"Načítám služby…", rect(15, 35, controlSize.width - 30, 35), 12, muted, 400, 0, DWRITE_TEXT_ALIGNMENT_CENTER);
            endControl();
        }
        return CDRF_DODEFAULT;
    }
    if (item.nmcd.dwDrawStage != CDDS_ITEMPREPAINT) return CDRF_DODEFAULT;
    const auto row = static_cast<int>(item.nmcd.dwItemSpec);
    const auto* record = serviceTable.record(row); if (!record) return CDRF_DODEFAULT;
    RECT bounds{}; ListView_GetItemRect(serviceTable.handle(), row, &bounds, LVIR_BOUNDS);
    DRAWITEMSTRUCT entry{}; entry.CtlType = ODT_LISTVIEW; entry.hwndItem = serviceTable.handle(); entry.hDC = item.nmcd.hdc; entry.rcItem = bounds;
    const auto state = ListView_GetItemState(serviceTable.handle(), row, LVIS_SELECTED | LVIS_FOCUSED);
    if (!beginControl(entry, state & LVIS_SELECTED ? D2D1::ColorF(0x28343b) : panel)) return CDRF_DODEFAULT;
    const auto h = controlSize.height, w = controlSize.width;
    if (state & LVIS_SELECTED) fill(rect(0, 0, 3, h), amber);
    fill(rect(0, h - 1, w, 1), line);
    for (int column = 0; column < 4; ++column) {
        RECT cell{}; Header_GetItemRect(serviceTable.header(), column, &cell);
        MapWindowPoints(serviceTable.header(), serviceTable.handle(), reinterpret_cast<POINT*>(&cell), 2);
        const auto x = (cell.left - bounds.left) / scale, cw = (cell.right - cell.left) / scale;
        surface->PushAxisAlignedClip(rect(x, 0, cw, h), D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
        if (column == 0) {
            text(record->title, rect(x + 18, 10, cw - 36, 19), 12, ink, 500);
            text(record->name, rect(x + 18, 33, cw - 36, 15), 10, muted);
        } else if (column == 1) {
            const auto color = record->state == SERVICE_RUNNING ? D2D1::ColorF(0x83bca0) : muted;
            const auto status = serviceStatus(record->state);
            const auto chipWidth = std::min(cw - 24, static_cast<float>(status.size()) * 5 + 22);
            fill(rect(x + 15, 18, chipWidth, 21), record->state == SERVICE_RUNNING ? D2D1::ColorF(0x263638) : D2D1::ColorF(0x293239), 4);
            drawBrush->SetColor(color);
            surface->FillEllipse(D2D1::Ellipse(D2D1::Point2F(x + 22, 28.5f), 2, 2), drawBrush);
            text(status, rect(x + 28, 22, chipWidth - 15, 16), 9, color);
        } else text(column == 2 ? serviceStartup(record->config) : record->pid ? std::to_wstring(record->pid) : L"—",
            rect(x + 18, 20, cw - 36, 18), 11, ink, 400, 0, column == 3 ? DWRITE_TEXT_ALIGNMENT_TRAILING : DWRITE_TEXT_ALIGNMENT_LEADING);
        surface->PopAxisAlignedClip();
    }
    if ((state & LVIS_FOCUSED) && focusFromKeyboard && GetFocus() == serviceTable.handle()) {
        drawBrush->SetColor(amber); surface->DrawRectangle(rect(.5f, .5f, w - 1, h - 1), drawBrush, 1);
    }
    endControl(); return CDRF_SKIPDEFAULT;
}

}
