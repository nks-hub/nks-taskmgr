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

bool Desktop::beginControl(const DRAWITEMSTRUCT& item)
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
            controlSize.height = 59;
            const auto logicalStart = static_cast<float>(item.itemID) * 59;
            offset.y += logicalStart - std::round(logicalStart * scale) / scale;
        }
    }
    controlTarget->SetTransform(D2D1::Matrix3x2F::Translation(offset.x, offset.y));
    controlTarget->BeginDraw();
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
    }
}

void Desktop::drawButton(const DRAWITEMSTRUCT& item)
{
    if (!beginControl(item)) return;
    const auto w = controlSize.width, h = controlSize.height;
    const bool navigation = item.CtlID == 1 || item.CtlID == 2;
    const bool caption = item.CtlID >= 10 && item.CtlID <= 12;
    const bool segmented = (item.CtlID >= 20 && item.CtlID <= 22) || item.CtlID == 30 || item.CtlID == 31;
    const bool selected = (item.CtlID == 1 && !performance) || (item.CtlID == 2 && performance)
        || (item.CtlID >= 20 && item.CtlID <= 22 && static_cast<int>(item.CtlID) - 20 == cpuView)
        || (item.CtlID == 30 && rangeSeconds == 60) || (item.CtlID == 31 && rangeSeconds == 600);
    const auto background = navigation || caption ? D2D1::ColorF(0x12181c) : item.CtlID == 40 ? D2D1::ColorF(0x171d21) : panel;
    fill(rect(0, 0, w, h), background);
    wchar_t label[80]{}; GetWindowTextW(item.hwndItem, label, 80);
    if (navigation) {
        if (selected) fill(rect(0, 0, w, h), D2D1::ColorF(0x3b3024), 9);
        if (!selected && hoveredControl == item.hwndItem) fill(rect(0, 0, w, h), D2D1::ColorF(0x232d34), 9);
        icon(item.CtlID, (w - 20) / 2, 13, 20, selected ? amber : muted);
        text(label, rect(0, 41, w, 16), 9, selected ? amber : muted, 400, 0, DWRITE_TEXT_ALIGNMENT_CENTER);
    } else if (caption) {
        text(label, rect(0, 11, w, 28), 17, muted, 400, 0, DWRITE_TEXT_ALIGNMENT_CENTER);
    } else if (segmented) {
        fill(rect(0, 0, w, h), selected ? D2D1::ColorF(0x222d34) : D2D1::ColorF(0x12181c), 4);
        text(label, rect(0, 6, w, 17), 11, selected ? ink : muted, 400, 0, DWRITE_TEXT_ALIGNMENT_CENTER);
    } else if (item.CtlID == 40) {
        text(L"Všechny procesy", rect(0, 6, w - 25, 19), 11, amber, 400, 0, DWRITE_TEXT_ALIGNMENT_TRAILING);
        icon(5, w - 16, 7, 13, amber);
    } else {
        fill(rect(.5f, .5f, w - 1, h - 1), hoveredControl == item.hwndItem ? D2D1::ColorF(0x232d34) : panel, 6);
        drawBrush->SetColor(line);
        surface->DrawRoundedRectangle(D2D1::RoundedRect(rect(.5f, .5f, w - 1, h - 1), 6, 6), drawBrush);
        if (item.CtlID == 3) {
            icon(paused.load() ? 4 : 3, 12, 11, 13, muted);
            text(label, rect(31, 9, w - 40, 18), 11, ink);
        } else text(label, rect(0, 9, w, 18), 11, ink, 400, 0, DWRITE_TEXT_ALIGNMENT_CENTER);
    }
    if ((item.itemState & ODS_FOCUS) && focusFromKeyboard) {
        drawBrush->SetColor(amber);
        surface->DrawRoundedRectangle(D2D1::RoundedRect(rect(1, 1, w - 2, h - 2), 6, 6), drawBrush, 1);
    }
    endControl();
}

void Desktop::drawResource(const DRAWITEMSTRUCT& entry)
{
    if (entry.itemID >= resources.size() || !beginControl(entry)) return;
    const auto w = controlSize.width, h = controlSize.height;
    const auto& item = resources[entry.itemID];
    const auto color = resourceColor(item.kind);
    fill(rect(0, 0, w, h), panel);
    if (item.id == selectedId) {
        fill(rect(0, .6f, w, 56), mix(color, panel, .08f), 7);
        drawBrush->SetColor(mix(color, line, .4f));
        surface->DrawRoundedRectangle(D2D1::RoundedRect(rect(.5f, 1.1f, w - 1, 55), 6.5f, 6.5f), drawBrush, 1);
    } else if (hoveredControl == resourceList && hoveredResource == static_cast<int>(entry.itemID)) fill(rect(0, .6f, w, 56), D2D1::ColorF(0x232d34), 7);
    text(item.title, rect(72, 7.6f, w - 82, 16), 12, ink, 600);
    text(item.description, rect(72, 23.6f, w - 82, 12), 9, muted);
    text(resourceValue(item), rect(72, 35.6f, w - 82, 14), 10, item.primary.valid() ? color : muted);
    const auto* series = history.find(item.id);
    const auto* uploads = item.kind == ResourceKind::network ? history.find(item.id + L":up") : nullptr;
    double maximum = item.kind == ResourceKind::memory ? item.capacity : 100;
    if (item.kind == ResourceKind::network) {
        maximum = 1000000;
        for (const auto* values : {series, uploads}) if (values) for (std::size_t i = 0; i < values->size(); ++i)
            if (values->at(i).metric.valid()) maximum = std::max(maximum, values->at(i).metric.value * 1.1);
    }
    graph(series, rect(10, 12.6f, 50, 31), color, maximum, false);
    if (uploads) graph(uploads, rect(10, 12.6f, 50, 31), resourceColor(ResourceKind::cpu), maximum, false);
    drawBrush->SetColor(mix(color, panel, .35f));
    surface->DrawLine(D2D1::Point2F(10, 43.6f), D2D1::Point2F(60, 43.6f), drawBrush, 1);
    if (!item.primary.valid()) text(L"—", rect(10, 19, 50, 20), 12, muted, 400, 0, DWRITE_TEXT_ALIGNMENT_CENTER);
    endControl();
}

void Desktop::drawConsumer(const DRAWITEMSTRUCT& item)
{
    const auto index = item.CtlID - 60;
    if (!processes || index >= consumers.size() || !consumers[index]) return;
    const auto found = std::find_if(processes->processes.begin(), processes->processes.end(), [&](const auto& p) { return p.identity == *consumers[index]; });
    if (found == processes->processes.end() || !beginControl(item)) return;
    const auto& process = *found;
    const auto w = controlSize.width, h = controlSize.height;
    const auto color = resourceColor(selectedResource() ? selectedResource()->kind : ResourceKind::cpu);
    fill(rect(0, 0, w, h), D2D1::ColorF(0x171d21));
    fill(rect(.5f, .5f, w - 1, h - 1), hoveredControl == item.hwndItem ? D2D1::ColorF(0x232d34) : panel, 9);
    drawBrush->SetColor(line);
    surface->DrawRoundedRectangle(D2D1::RoundedRect(rect(.5f, .5f, w - 1, h - 1), 9, 9), drawBrush);
    text(L"0" + std::to_wstring(index + 1), rect(18, 28, 20, 16), 9, muted);
    const std::array backgrounds{0xdcf2f6, 0xe1effe, 0xfcebd8, 0xe9edf2};
    const std::array foregrounds{0x167a9c, 0x176bc4, 0xad631d, 0x526273};
    unsigned hash = 0; for (wchar_t c : process.name) hash = hash * 31 + c;
    const auto palette = hash % backgrounds.size();
    fill(rect(38, 21, 30, 30), D2D1::ColorF(backgrounds[palette]), 8);
    auto initial = process.name.empty() ? L"?" : process.name.substr(0, 1);
    std::transform(initial.begin(), initial.end(), initial.begin(), [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
    text(initial, rect(38, 27, 30, 20), 12, D2D1::ColorF(foregrounds[palette]), 700, 0, DWRITE_TEXT_ALIGNMENT_CENTER);
    text(process.name, rect(79, 19, w - 175, 17), 11, ink, 600);
    text(L"PID " + std::to_wstring(process.identity.pid), rect(79, 41, w - 175, 15), 9, muted);
    const bool memory = selectedResource() && selectedResource()->kind == ResourceKind::memory;
    const auto value = memory ? decimal(process.counters.workingSetBytes / 1048576., L" MiB", 0) : decimal(process.cpuPercent.value, L" %");
    text(value, rect(w - 111, 28, 69, 20), 12, ink, 400, 0, DWRITE_TEXT_ALIGNMENT_TRAILING);
    icon(5, w - 31, 29, 13, muted);
    fill(rect(18, h - 12, w - 36, 2), line);
    double top = memory ? static_cast<double>(process.counters.workingSetBytes) : process.cpuPercent.value;
    if (consumers[0]) {
        const auto first = std::find_if(processes->processes.begin(), processes->processes.end(), [&](const auto& p) { return p.identity == *consumers[0]; });
        if (first != processes->processes.end()) top = memory ? static_cast<double>(first->counters.workingSetBytes) : first->cpuPercent.value;
    }
    const auto amount = memory ? static_cast<double>(process.counters.workingSetBytes) : process.cpuPercent.value;
    if (top > 0) fill(rect(18, h - 12, (w - 36) * static_cast<float>(amount / top), 2), mix(color, panel, .5f));
    endControl();
}

}
