#include "desktop.hpp"
#include "theme.hpp"
#include <algorithm>
#include <cmath>
#include <iomanip>
#include <sstream>

namespace nks {
using namespace theme;

std::wstring decimal(double value, const wchar_t* unit, int digits)
{
    std::wostringstream text;
    text << std::fixed << std::setprecision(digits) << value << unit;
    auto result = text.str();
    std::replace(result.begin(), result.end(), L'.', L',');
    const auto numericEnd = result.find_first_not_of(L"0123456789-+");
    if (numericEnd != std::wstring::npos) {
        const auto start = !result.empty() && (result[0] == L'-' || result[0] == L'+') ? 1U : 0U;
        for (auto at = numericEnd; at > start + 3; at -= 3) result.insert(at - 3, 1, L'\u00a0');
    }
    return result;
}

D2D1_COLOR_F resourceColor(ResourceKind kind)
{
    switch (kind) {
    case ResourceKind::cpu: return D2D1::ColorF(0x80c5c0);
    case ResourceKind::memory: return D2D1::ColorF(0xabb0f1);
    case ResourceKind::disk: return D2D1::ColorF(0x83bca0);
    case ResourceKind::gpu: return D2D1::ColorF(0xcc9bb6);
    default: return D2D1::ColorF(0xdfaf76);
    }
}

std::wstring resourceValue(const ResourceRecord& item)
{
    if (!item.primary.valid()) return item.primary.state == SampleState::stale ? L"Zastaralá data" : item.status == L"Odpojeno" ? item.status
        : item.primary.state == SampleState::first ? L"První vzorek" : L"Měření nedostupné";
    if (item.kind == ResourceKind::memory) return decimal(item.primary.value / 1073741824, L" GiB");
    if (item.kind == ResourceKind::network) return L"↓ " + decimal(item.primary.value / 1000000, L"  ↑ ")
        + (item.secondary.valid() ? decimal(item.secondary.value / 1000000, L" Mbit/s") : L"— Mbit/s");
    return decimal(item.primary.value, L" %");
}

DWRITE_TEXT_METRICS Desktop::text(const std::wstring& value, D2D1_RECT_F box, float size, D2D1_COLOR_F color,
    int weight, float spacing, DWRITE_TEXT_ALIGNMENT alignment)
{
    if (!weight) weight = size == 42 ? 400 : size >= 16 ? 600 : 400;
    auto& format = formats[{size, weight}];
    if (!format && FAILED(writing->CreateTextFormat(size == 42 ? L"Bahnschrift" : L"Segoe UI", nullptr,
        static_cast<DWRITE_FONT_WEIGHT>(weight), DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, size, L"cs-CZ", &format))) return {};
    format->SetWordWrapping(box.bottom - box.top > size * 2.2f ? DWRITE_WORD_WRAPPING_WRAP : DWRITE_WORD_WRAPPING_NO_WRAP);
    format->SetTextAlignment(alignment);
    drawBrush->SetColor(color);
    Microsoft::WRL::ComPtr<IDWriteTextLayout> layout;
    if (FAILED(writing->CreateTextLayout(value.c_str(), static_cast<UINT32>(value.size()), format.Get(),
        std::max(0.f, box.right - box.left), std::max(0.f, box.bottom - box.top), &layout))) return {};
    if (spacing) {
        Microsoft::WRL::ComPtr<IDWriteTextLayout1> advanced;
        if (SUCCEEDED(layout.As(&advanced))) advanced->SetCharacterSpacing(0, spacing, 0, {0, static_cast<UINT32>(value.size())});
    }
    if (format->GetWordWrapping() == DWRITE_WORD_WRAPPING_NO_WRAP) {
        auto& ellipsis = ellipses[{size, weight}];
        if (!ellipsis) writing->CreateEllipsisTrimmingSign(format.Get(), &ellipsis);
        const DWRITE_TRIMMING trimming{DWRITE_TRIMMING_GRANULARITY_CHARACTER, 0, 0};
        if (ellipsis) layout->SetTrimming(&trimming, ellipsis.Get());
    }
    surface->PushAxisAlignedClip(box, D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
    surface->DrawTextLayout(D2D1::Point2F(box.left, box.top), layout.Get(), drawBrush);
    surface->PopAxisAlignedClip();
    DWRITE_TEXT_METRICS metrics{};
    layout->GetMetrics(&metrics);
    return metrics;
}

void Desktop::fill(D2D1_RECT_F box, D2D1_COLOR_F color, float radius)
{
    drawBrush->SetColor(color);
    if (radius) surface->FillRoundedRectangle(D2D1::RoundedRect(box, radius, radius), drawBrush);
    else surface->FillRectangle(box, drawBrush);
}

void Desktop::drawPolyline(std::span<const D2D1_POINT_2F> vertices, float width)
{
    if (vertices.size() < 2) return;
    Microsoft::WRL::ComPtr<ID2D1PathGeometry> geometry;
    Microsoft::WRL::ComPtr<ID2D1GeometrySink> points;
    if (FAILED(factory->CreatePathGeometry(&geometry)) || FAILED(geometry->Open(&points))) return;
    points->BeginFigure(vertices.front(), D2D1_FIGURE_BEGIN_HOLLOW);
    points->AddLines(vertices.data() + 1, static_cast<UINT>(vertices.size() - 1));
    points->EndFigure(D2D1_FIGURE_END_OPEN);
    if (SUCCEEDED(points->Close())) surface->DrawGeometry(geometry.Get(), drawBrush, width, roundedStroke.Get());
}

void Desktop::graph(const HistorySeries* series, D2D1_RECT_F box, D2D1_COLOR_F color, double maximum, bool grid, bool area)
{
    if (grid) {
        drawBrush->SetColor(D2D1::ColorF(0x2c383f));
        for (unsigned i = 1; i < 5; ++i) {
            const auto y = box.top + (box.bottom - box.top) * i / 5;
            surface->DrawLine(D2D1::Point2F(box.left, y), D2D1::Point2F(box.right, y), drawBrush, 1);
        }
        for (unsigned i = 1; i < 10; ++i) {
            const auto x = box.left + (box.right - box.left) * i / 10;
            surface->DrawLine(D2D1::Point2F(x, box.top), D2D1::Point2F(x, box.bottom), drawBrush, 1);
        }
    }
    if (!series || !series->size() || maximum <= 0) return;
    const auto end = paused.load() ? series->at(series->size() - 1).time : std::chrono::steady_clock::now();
    const auto start = end - std::chrono::seconds(rangeSeconds);
    std::vector<D2D1_POINT_2F> segment;
    const auto flush = [&] {
        if (segment.empty()) return;
        Microsoft::WRL::ComPtr<ID2D1PathGeometry> geometry;
        Microsoft::WRL::ComPtr<ID2D1GeometrySink> sink;
        if (area && SUCCEEDED(factory->CreatePathGeometry(&geometry)) && SUCCEEDED(geometry->Open(&sink))) {
            sink->BeginFigure(D2D1::Point2F(segment.front().x, box.bottom), D2D1_FIGURE_BEGIN_FILLED);
            sink->AddLines(segment.data(), static_cast<UINT>(segment.size()));
            sink->AddLine(D2D1::Point2F(segment.back().x, box.bottom));
            sink->EndFigure(D2D1_FIGURE_END_CLOSED);
            if (SUCCEEDED(sink->Close())) {
                const D2D1_GRADIENT_STOP stops[]{{0, D2D1::ColorF(color.r, color.g, color.b, .28f)}, {1, D2D1::ColorF(color.r, color.g, color.b, .035f)}};
                Microsoft::WRL::ComPtr<ID2D1GradientStopCollection> collection;
                Microsoft::WRL::ComPtr<ID2D1LinearGradientBrush> gradient;
                if (SUCCEEDED(surface->CreateGradientStopCollection(stops, 2, &collection))
                    && SUCCEEDED(surface->CreateLinearGradientBrush(D2D1::LinearGradientBrushProperties(
                        D2D1::Point2F(box.left, std::min_element(segment.begin(), segment.end(), [](auto a, auto b) { return a.y < b.y; })->y),
                        D2D1::Point2F(box.left, box.bottom)), collection.Get(), &gradient)))
                    surface->FillGeometry(geometry.Get(), gradient.Get());
            }
        }
        drawBrush->SetColor(color);
        if (segment.size() == 1) surface->FillEllipse(D2D1::Ellipse(segment[0], 2, 2), drawBrush);
        if (segment.size() > 1) drawPolyline(segment, 1.7f);
        segment.clear();
    };
    for (std::size_t i = 0; i < series->size(); ++i) {
        const auto& point = series->at(i);
        if (point.time < start) continue;
        if (!point.metric.valid()) { flush(); continue; }
        if (i && point.time - series->at(i - 1).time > std::chrono::milliseconds(2500)) flush();
        const auto x = box.left + static_cast<float>(std::chrono::duration<double>(point.time - start).count() / rangeSeconds) * (box.right - box.left);
        const auto y = box.bottom - static_cast<float>(std::clamp(point.metric.value / maximum, 0.0, 1.0)) * (box.bottom - box.top);
        segment.push_back(D2D1::Point2F(x, y));
    }
    flush();
}

void Desktop::paint()
{
    PAINTSTRUCT paint{}; BeginPaint(window, &paint);
    RECT client{}; GetClientRect(window, &client);
    if (!target) {
        const auto properties = D2D1::RenderTargetProperties();
        if (FAILED(factory->CreateHwndRenderTarget(properties,
            D2D1::HwndRenderTargetProperties(window, D2D1::SizeU(client.right, client.bottom)), &target))) {
            EndPaint(window, &paint); return;
        }
        target->SetDpi(96 * scale, 96 * scale);
        target->SetTextRenderingParams(textRendering.Get());
        if (FAILED(target->CreateSolidColorBrush(ink, &brush))) {
            brush.Reset(); target.Reset();
            EndPaint(window, &paint); return;
        }
    }
    surface = target.Get(); drawBrush = brush.Get();
    target->BeginDraw();
    target->Clear(D2D1::ColorF(0x171d21));
    const auto width = client.right / scale, height = client.bottom / scale;
    fill(rect(0, 0, width, 49), D2D1::ColorF(0x12181c));
    fill(rect(0, 49, 88, height - 49), D2D1::ColorF(0x12181c));
    fill(rect(0, 48, width, 1), line);
    fill(rect(87, 49, 1, height - 49), line);
    fill(rect(88, height - 30, width - 88, 1), line);
    icon(2, 23, 16, 18, amber);
    text(L"NKS", rect(51, 16, 35, 22), 12, ink, 700);
    text(L"Task Manager", rect(82, 16, 110, 22), 12, muted);
    fill(rect(194, 17, 1, 14), line);
    text(L"Lokální počítač", rect(218, 18, 180, 20), 10, muted);
    text(L"n", rect(31, 62, 30, 40), 32, ink, 700);
    text(L".", rect(46, 62, 18, 40), 32, amber, 700);
    fill(rect(0, performance ? 220.f : 150.f, 3, 20), amber, 1);
    text(performance ? L"Výkon" : L"Procesy", rect(118, 72, 300, 38), 28, ink, 600, -.7f);
    text(performance ? L"Živý přehled všech prostředků" : L"Aplikace, prostředky a jejich souvislosti.", rect(119, 117, 600, 19), 11, muted);
    if (performance) paintPerformance(width, height); else paintProcesses(width, height);
    auto status = paused.load() ? L"Pozastaveno" : processes ? L"Živá data" : L"Načítám procesy";
    std::wstring footer = status;
    if (processes) footer += L"  ·  " + std::to_wstring(processes->processes.size()) + L" procesů  ·  stáří "
        + decimal(std::chrono::duration<double>(std::chrono::steady_clock::now() - processes->measuredAt).count(), L" s");
    if (!processError.empty()) footer = L"Sběr procesů selhal: " + processError;
    else if (!resourceError.empty()) footer += L"  ·  Sběr zařízení: " + resourceError;
    text(footer, rect(113, height - 21, width - 140, 18), 9, muted);
    if (target->EndDraw() == D2DERR_RECREATE_TARGET) { brush.Reset(); target.Reset(); }
    EndPaint(window, &paint);
}

void Desktop::paintProcesses(float width, float height)
{
    const auto y = height - 130;
    fill(rect(118, y, width - 150, 87), panel, 8);
    const auto* item = table.selected();
    if (!item) {
        text(L"Vyberte proces", rect(139, y + 14, 500, 28), 20, ink);
        text(std::to_wstring(table.count()) + L" zobrazených procesů · řazení kliknutím na sloupec · Ctrl a Shift pro více výběrů", rect(139, y + 49, width - 180, 24), 12, muted);
        return;
    }
    text(item->name, rect(138, y + 13, 360, 31), 21, ink);
    text(L"PID " + std::to_wstring(item->identity.pid) + L"  ·  " + std::to_wstring(item->counters.threads) + L" vláken  ·  "
        + std::to_wstring(item->counters.handles) + L" popisovačů", rect(139, y + 50, 480, 23), 12, muted);
    text(L"CPU", rect(width - 400, y + 13, 140, 23), 12, muted);
    text(processStale ? L"Zastaralé" : item->cpuPercent.valid() ? decimal(item->cpuPercent.value, L" %") : L"Nezměřeno", rect(width - 400, y + 39, 140, 32), 23, resourceColor(ResourceKind::cpu));
    text(L"Pracovní sada", rect(width - 220, y + 13, 160, 23), 12, muted);
    text(processStale ? L"Zastaralé" : decimal(item->counters.workingSetBytes / 1048576., L" MiB"), rect(width - 220, y + 39, 175, 32), 23, resourceColor(ResourceKind::memory));
}

}
