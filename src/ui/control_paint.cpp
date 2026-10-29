#include "desktop.hpp"
#include "theme.hpp"
#include <algorithm>
#include <cwctype>
#include <cmath>

namespace nks {
using namespace theme;
namespace {
D2D1_COLOR_F mix(D2D1_COLOR_F a, D2D1_COLOR_F b, float amount)
{
    return D2D1::ColorF(a.r * amount + b.r * (1 - amount), a.g * amount + b.g * (1 - amount), a.b * amount + b.b * (1 - amount));
}
}

bool Desktop::beginControl(const DRAWITEMSTRUCT& item, D2D1_COLOR_F background)
{
    if (!controlTarget) {
        const auto properties = D2D1::RenderTargetProperties(D2D1_RENDER_TARGET_TYPE_DEFAULT,
            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_IGNORE), 96 * scale, 96 * scale);
        if (FAILED(factory->CreateDCRenderTarget(&properties, &controlTarget))) return false;
        if (FAILED(controlTarget->CreateSolidColorBrush(ink, &controlBrush))) { controlTarget.Reset(); return false; }
        controlTarget->SetTextRenderingParams(textRendering.Get());
    }
    controlTarget->SetDpi(96 * scale, 96 * scale);
    if (FAILED(controlTarget->BindDC(item.hDC, &item.rcItem))) return false;
    previousSurface = surface; previousBrush = drawBrush;
    surface = controlTarget.Get(); drawBrush = controlBrush.Get();
    const auto found = controlBounds.find(item.hwndItem);
    auto offset = D2D1::Point2F();
    controlSize = D2D1::SizeF((item.rcItem.right - item.rcItem.left) / scale, (item.rcItem.bottom - item.rcItem.top) / scale);
    if (found != controlBounds.end()) {
        const auto& bounds = found->second;
        offset = D2D1::Point2F(bounds.left - std::floor(bounds.left * scale) / scale, bounds.top - std::floor(bounds.top * scale) / scale);
        controlSize = D2D1::SizeF(bounds.right - bounds.left, bounds.bottom - bounds.top);
        if (item.CtlType == ODT_LISTBOX) {
            const auto itemHeight = item.CtlID == 103 ? 44.f : 59.f;
            controlSize.height = itemHeight;
            const auto logicalStart = static_cast<float>(item.itemID) * itemHeight;
            offset.y += logicalStart - std::round(logicalStart * scale) / scale;
        } else if (item.CtlType == ODT_LISTVIEW) {
            controlSize = D2D1::SizeF((item.rcItem.right - item.rcItem.left) / scale, (item.rcItem.bottom - item.rcItem.top) / scale);
        }
    }
    controlTarget->SetTransform(D2D1::Matrix3x2F::Translation(offset.x, offset.y));
    controlTarget->BeginDraw();
    controlTarget->Clear(background);
    return true;
}

void Desktop::endControl()
{
    const auto status = controlTarget->EndDraw();
    surface = previousSurface; drawBrush = previousBrush;
    if (status == D2DERR_RECREATE_TARGET) { controlBrush.Reset(); controlTarget.Reset(); }
}

void Desktop::icon(unsigned kind, float x, float y, float size, D2D1_COLOR_F color)
{
    const auto p = [&](float px, float py) { return D2D1::Point2F(x + px * size / 24, y + py * size / 24); };
    drawBrush->SetColor(color);
    const auto stroke = size * 1.6f / 24;
    const auto segment = [&](float x1, float y1, float x2, float y2) { surface->DrawLine(p(x1, y1), p(x2, y2), drawBrush, stroke, roundedStroke.Get()); };
    switch (kind) {
    case 1:
        surface->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(p(4, 3).x, p(4, 3).y, p(20, 21).x, p(20, 21).y), size / 12, size / 12), drawBrush, stroke);
        segment(8, 8, 16, 8); segment(8, 12, 16, 12); segment(8, 16, 13, 16); break;
    case 2:
        {
            segment(3, 19, 21, 19);
            const std::array points{p(4, 15), p(8, 9), p(12, 13), p(16, 4), p(20, 10)};
            drawPolyline(points, stroke); break;
        }
    case 3: segment(8, 5, 8, 19); segment(16, 5, 16, 19); break;
    case 4: segment(8, 5, 19, 12); segment(19, 12, 8, 19); segment(8, 19, 8, 5); break;
    case 5: {
        segment(5, 12, 19, 12);
        const std::array points{p(14, 7), p(19, 12), p(14, 17)};
        drawPolyline(points, stroke); break;
    }
    case 6:
        for (float row : {4.f, 14.f}) {
            surface->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(p(4, row).x, p(4, row).y, p(20, row + 6).x, p(20, row + 6).y), size / 12, size / 12), drawBrush, stroke);
            segment(7, row + 3, 8, row + 3); segment(13, row + 3, 17, row + 3);
        }
        break;
    }
}

void Desktop::drawButton(const DRAWITEMSTRUCT& item)
{
    const bool navigation = item.CtlID == 1 || item.CtlID == 2 || item.CtlID == 7;
    const bool caption = item.CtlID >= 10 && item.CtlID <= 12;
    const auto background = navigation || caption ? D2D1::ColorF(0x12181c) : item.CtlID == 40 ? D2D1::ColorF(0x171d21) : panel;
    if (!beginControl(item, background)) return;
    const auto w = controlSize.width, h = controlSize.height;
    const bool segmented = (item.CtlID >= 20 && item.CtlID <= 22) || item.CtlID == 30 || item.CtlID == 31
        || (item.CtlID >= 70 && item.CtlID <= 72) || (item.CtlID >= 110 && item.CtlID <= 113) || item.CtlID == 130 || item.CtlID == 131;
    const bool selected = (item.CtlID == 1 && view == DesktopView::processes) || (item.CtlID == 2 && view == DesktopView::performance)
        || (item.CtlID == 7 && view == DesktopView::services)
        || (item.CtlID >= 110 && item.CtlID <= 113 && static_cast<int>(item.CtlID) - 110 == static_cast<int>(serviceTable.filterMode()))
        || (item.CtlID >= 130 && item.CtlID <= 131 && static_cast<int>(item.CtlID) - 130 == serviceTab)
        || (item.CtlID >= 20 && item.CtlID <= 22 && static_cast<int>(item.CtlID) - 20 == cpuView)
        || (item.CtlID == 30 && rangeSeconds == 60) || (item.CtlID == 31 && rangeSeconds == 600);
    const bool processFilter = item.CtlID >= 70 && item.CtlID <= 72;
    const bool activeFilter = processFilter && static_cast<int>(item.CtlID) - 70 == static_cast<int>(table.mode());
    fill(rect(0, 0, w, h), background);
    wchar_t label[80]{}; GetWindowTextW(item.hwndItem, label, 80);
    if (navigation) {
        if (selected) fill(rect(0, 0, w, h), D2D1::ColorF(0x3b3024), 9);
        if (!selected && hoveredControl == item.hwndItem) fill(rect(0, 0, w, h), D2D1::ColorF(0x232d34), 9);
        icon(item.CtlID == 7 ? 6 : item.CtlID, (w - 20) / 2, 13, 20, selected ? amber : muted);
        text(label, rect(0, 41, w, 16), 9, selected ? amber : muted, 400, 0, DWRITE_TEXT_ALIGNMENT_CENTER);
    } else if (caption) {
        const auto left = item.CtlID == 12 ? .5f : 0;
        text(label, rect(left, 13, w, 28), 17, muted, 400, 0, DWRITE_TEXT_ALIGNMENT_CENTER);
    } else if (segmented) {
        fill(rect(0, 0, w, h), selected || activeFilter ? D2D1::ColorF(0x222d34) : D2D1::ColorF(0x12181c), 4);
        text(label, rect(0, processFilter ? 7.f : 6.f, w, 17), 11, selected || activeFilter ? ink : muted, 400, 0, DWRITE_TEXT_ALIGNMENT_CENTER);
    } else if (item.CtlID == 40) {
        text(L"Všechny procesy", rect(0, 6, w - 25, 19), 11, amber, 400, 0, DWRITE_TEXT_ALIGNMENT_TRAILING);
        icon(5, w - 16, 7, 13, amber);
    } else {
        fill(rect(.5f, .5f, w - 1, h - 1), hoveredControl == item.hwndItem ? D2D1::ColorF(0x232d34) : panel, 6);
        drawBrush->SetColor(line);
        surface->DrawRoundedRectangle(D2D1::RoundedRect(rect(.5f, .5f, w - 1, h - 1), 6, 6), drawBrush);
        if (item.CtlID == 3) {
            icon(monitor.paused() ? 4 : 3, 12, 11, 13, muted);
            text(label, rect(31, 9, w - 40, 18), 11, ink);
        } else text(label, rect(0, 9, w, 18), 11, (item.itemState & ODS_DISABLED) ? muted : ink, 400, 0, DWRITE_TEXT_ALIGNMENT_CENTER);
    }
    if ((item.itemState & ODS_FOCUS) && focusFromKeyboard) {
        drawBrush->SetColor(amber);
        surface->DrawRoundedRectangle(D2D1::RoundedRect(rect(1, 1, w - 2, h - 2), 6, 6), drawBrush, 1);
    }
    endControl();
}

void Desktop::drawResource(const DRAWITEMSTRUCT& entry)
{
    if (entry.itemID >= resources.size() || !beginControl(entry, panel)) return;
    const auto w = controlSize.width, h = controlSize.height;
    const auto& item = resources[entry.itemID];
    const auto color = resourceColor(item.kind);
    fill(rect(0, 0, w, h), panel);
    if (item.id == selectedId) {
        fill(rect(0, .6f, w, 56), mix(color, panel, .08f), 7);
        drawBrush->SetColor(mix(color, line, .4f));
        surface->DrawRoundedRectangle(D2D1::RoundedRect(rect(.5f, 1.1f, w - 1, 55), 6.5f, 6.5f), drawBrush, 1);
    } else if (hoveredControl == resourceList && hoveredResource == static_cast<int>(entry.itemID)) fill(rect(0, .6f, w, 56), D2D1::ColorF(0x232d34), 7);
    const auto compact = performanceLayout().compact;
    const auto textX = compact ? 49.f : 72.f;
    text(item.title, rect(textX, compact ? 13.6f : 7.6f, w - textX - 10, 16), 12, ink, 600);
    if (!compact) text(item.description, rect(textX, 23.6f, w - textX - 10, 12), 9, muted);
    if (compact && item.kind == ResourceKind::network && item.primary.valid()) {
        text(L"↓ " + decimal(item.primary.value / 1000000, L" Mbit/s"), rect(textX, 29.6f, w - textX - 10, 13), 9, color);
        text(L"↑ " + (item.secondary.valid() ? decimal(item.secondary.value / 1000000, L" Mbit/s") : L"—"),
            rect(textX, 42.6f, w - textX - 10, 13), 9, resourceColor(ResourceKind::cpu));
    } else text(resourceValue(item), rect(textX, compact ? 29.6f : 35.6f, w - textX - 10, 14), 10, item.primary.valid() ? color : muted);
    const auto* series = history.find(item.id);
    const auto* uploads = item.kind == ResourceKind::network ? history.find(item.id + L":up") : nullptr;
    double maximum = item.kind == ResourceKind::memory ? item.capacity : 100;
    if (item.kind == ResourceKind::network) {
        maximum = 1000000;
        for (const auto* values : {series, uploads}) if (values) for (std::size_t i = 0; i < values->size(); ++i)
            if (values->at(i).metric.valid()) maximum = std::max(maximum, values->at(i).metric.value * 1.1);
    }
    const auto preview = compact ? rect(6, 15.6f, 36, 25) : rect(10, 12.6f, 50, 31);
    graph(series, preview, color, maximum, false);
    if (uploads) graph(uploads, preview, resourceColor(ResourceKind::cpu), maximum, false);
    drawBrush->SetColor(mix(color, panel, .35f));
    surface->DrawLine(D2D1::Point2F(preview.left, preview.bottom), D2D1::Point2F(preview.right, preview.bottom), drawBrush, 1);
    if (!item.primary.valid()) text(L"—", rect(preview.left, preview.top + 6, preview.right - preview.left, 20), 12, muted, 400, 0, DWRITE_TEXT_ALIGNMENT_CENTER);
    endControl();
}

void Desktop::drawConsumer(const DRAWITEMSTRUCT& item)
{
    const auto index = item.CtlID - 60;
    if (!processes || index >= consumers.size() || !consumers[index]) return;
    const auto found = std::find_if(processes->processes.begin(), processes->processes.end(), [&](const auto& p) { return p.identity == *consumers[index]; });
    if (found == processes->processes.end() || !beginControl(item, D2D1::ColorF(0x171d21))) return;
    const auto& process = *found;
    const auto w = controlSize.width, h = controlSize.height;
    RECT client{}; GetClientRect(window, &client);
    const auto width = client.right / scale;
    const bool narrow = width <= 1250, compact = width <= 1000;
    const auto padding = narrow ? 12.f : 18.f;
    const auto color = resourceColor(selectedResource() ? selectedResource()->kind : ResourceKind::cpu);
    fill(rect(0, 0, w, h), D2D1::ColorF(0x171d21));
    fill(rect(.5f, .5f, w - 1, h - 1), hoveredControl == item.hwndItem ? D2D1::ColorF(0x232d34) : panel, 9);
    drawBrush->SetColor(line);
    surface->DrawRoundedRectangle(D2D1::RoundedRect(rect(.5f, .5f, w - 1, h - 1), 9, 9), drawBrush);
    if (!narrow) text(L"0" + std::to_wstring(index + 1), rect(18, 28, 20, 16), 9, muted);
    const auto iconX = narrow ? 13.f : 38.f;
    if (!compact) processIcon(process.name, iconX, 21, 30);
    const auto textX = compact ? 13.f : narrow ? 51.f : 79.f;
    const auto valueRight = w - (narrow ? 13.f : 42.f);
    const auto nameWidth = compact ? w - 26 : valueRight - 80 - textX;
    text(process.name, rect(textX, compact ? 14.f : 19.f, nameWidth, 17), compact ? 10.f : 11.f, ink, 600);
    text(L"PID " + std::to_wstring(process.identity.pid), rect(textX, compact ? 38.f : 41.f, compact ? w - 92 : nameWidth, 15), 9, muted);
    const bool memory = selectedResource() && selectedResource()->kind == ResourceKind::memory;
    const auto value = memory ? decimal(process.counters.workingSetBytes / 1048576., L" MiB", 0) : decimal(process.cpuPercent.value, L" %");
    text(value, rect(valueRight - 69, compact ? 36.f : 28.f, 69, 20), narrow ? 10.f : 12.f, ink, 400, 0, DWRITE_TEXT_ALIGNMENT_TRAILING);
    if (!narrow) icon(5, w - 31, 29, 13, muted);
    fill(rect(padding, h - 12, w - padding * 2, 2), line);
    double top = memory ? static_cast<double>(process.counters.workingSetBytes) : process.cpuPercent.value;
    if (consumers[0]) {
        const auto first = std::find_if(processes->processes.begin(), processes->processes.end(), [&](const auto& p) { return p.identity == *consumers[0]; });
        if (first != processes->processes.end()) top = memory ? static_cast<double>(first->counters.workingSetBytes) : first->cpuPercent.value;
    }
    const auto amount = memory ? static_cast<double>(process.counters.workingSetBytes) : process.cpuPercent.value;
    if (top > 0) fill(rect(padding, h - 12, (w - padding * 2) * static_cast<float>(amount / top), 2), mix(color, panel, .5f));
    endControl();
}

}
