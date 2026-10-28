#include "desktop.hpp"
#include "theme.hpp"
#include <algorithm>
#include <numeric>

namespace nks {
using namespace theme;

void Desktop::paintPerformance(float width, float height)
{
    const auto card = [&](D2D1_RECT_F bounds, float radius = 11) {
        fill(bounds, panel, radius);
        drawBrush->SetColor(line);
        const auto stroke = D2D1::RectF(bounds.left + .5f, bounds.top + .5f, bounds.right - .5f, bounds.bottom - .5f);
        surface->DrawRoundedRectangle(D2D1::RoundedRect(stroke, radius - .5f, radius - .5f), drawBrush, 1);
    };
    const auto frame = performanceLayout();
    card(rect(frame.inventoryX, frame.top, frame.inventoryWidth, frame.bottom - frame.top), 10);
    text(L"Prostředky", rect(frame.inventoryX + 19, 170.59375f, 110, 22), 13, ink, 600);
    text(std::to_wstring(resources.size()) + L" prostředků", rect(frame.inventoryX + frame.inventoryWidth - 91, 173.59375f, 71, 18), 9, muted, 400, 0, DWRITE_TEXT_ALIGNMENT_TRAILING);
    text(paused.load() ? L"Pozastavený přehled" : L"Živá data · všechny prostředky", rect(frame.inventoryX + 19, height - 76, frame.inventoryWidth - 38, 18), 9, muted);
    const auto* item = selectedResource();
    if (!item) { text(L"Načítám prostředky…", rect(433, 190, 400, 40), 20, muted); return; }
    const auto color = resourceColor(item->kind);
    surface->PushAxisAlignedClip(D2D1::RectF(frame.detailX, frame.top, frame.detailRight - (frame.maxScroll > 0 ? 17 : 0), frame.bottom), D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
    surface->SetTransform(D2D1::Matrix3x2F::Translation(0, -detailScroll));
    const float x = frame.detailX, y = frame.top, available = frame.available;
    const float factsWidth = frame.factsWidth;
    const float w = frame.chartWidth, h = frame.chartHeight;
    const float fx = frame.factsX, factsY = frame.factsY;
    card(rect(x, y, w, h));
    card(rect(fx, factsY, factsWidth, frame.factsHeight));
    text(item->kind == ResourceKind::memory ? L"FYZICKÁ PAMĚŤ" : L"AKTIVITA PROSTŘEDKU", rect(x + 23, y + 21, w - 164, 18), 9, muted, 600, 1.6f);
    const auto title = text(item->title, rect(x + 23, y + 45, w - 46, 31), 20, ink, 600, -.3f);
    const auto captionX = x + 23 + title.widthIncludingTrailingWhitespace + 17;
    const auto modelCaption = item->kind == ResourceKind::memory ? decimal(item->capacity / 1073741824., L" GiB celkem") : item->description;
    if (width > 1000) text(modelCaption, D2D1::RectF(captionX, y + 56, x + w - 161, y + 75), 11, muted);
    fill(rect(x + w - 139, y + 29, 116, 35), D2D1::ColorF(0x12181c), 7);
    drawBrush->SetColor(line);
    surface->DrawRoundedRectangle(D2D1::RoundedRect(rect(x + w - 138.5f, y + 29.5f, 115, 34), 6.5f, 6.5f), drawBrush);
    double divisor = item->kind == ResourceKind::memory ? 1073741824. : item->kind == ResourceKind::network ? 1000000. : 1.;
    const auto unit = item->kind == ResourceKind::memory ? L"GiB" : item->kind == ResourceKind::network ? L"Mbit/s" : L"%";
    const auto reading = item->primary.valid() ? decimal(item->primary.value / divisor, L"") : resourceValue(*item);
    const auto quantity = text(reading, rect(x + 23, y + 88, w - 46, 55), item->primary.valid() ? frame.compact ? 34.f : 42.f : 21.f, ink, 400, item->primary.valid() ? -1.f : 0);
    if (item->primary.valid()) {
        const auto unitX = x + 23 + quantity.widthIncludingTrailingWhitespace + 8;
        const auto unitSize = text(unit, rect(unitX, y + 111, 70, 22), 13, muted);
        const auto note = item->kind == ResourceKind::memory && item->capacity > 0
            ? decimal(item->primary.value / item->capacity * 100, L" % kapacity", 0) : L"aktuální hodnota";
        const auto noteX = unitX + unitSize.widthIncludingTrailingWhitespace + 14;
        const auto noteWidth = std::min(100.f, x + w - 90 - noteX);
        if (noteWidth > 60) {
            auto noteColor = color; noteColor.a = .25f; drawBrush->SetColor(noteColor);
            surface->DrawRoundedRectangle(D2D1::RoundedRect(rect(noteX, y + 109, noteWidth, 24), 4, 4), drawBrush);
            text(note, rect(noteX + 7, y + 113, noteWidth - 14, 16), 10, color);
        }
    }
    const float graphY = item->kind == ResourceKind::cpu ? y + 179 : y + 159;
    const float graphBottom = y + h - (item->kind == ResourceKind::memory ? 160 : 89);
    const auto plot = D2D1::RectF(x + 23, graphY, x + w - 23, graphBottom);
    const auto* series = history.find(item->id);
    double maximum = item->kind == ResourceKind::memory ? item->capacity : 100;
    if (item->kind == ResourceKind::network) {
        maximum = 1000000;
        for (const auto key : {item->id, item->id + L":up"})
            if (const auto* values = history.find(key)) for (std::size_t i = 0; i < values->size(); ++i)
                if (values->at(i).metric.valid()) maximum = std::max(maximum, values->at(i).metric.value * 1.1);
    }
    text(item->kind == ResourceKind::memory ? decimal(item->capacity / divisor, L" GiB")
        : item->kind == ResourceKind::network ? decimal(maximum / 1000000, L" Mbit/s") : L"100 %",
        rect(x + w - 123, y + 115, 100, 16), 9, muted, 400, 0, DWRITE_TEXT_ALIGNMENT_TRAILING);
    if (item->kind == ResourceKind::cpu && cpuView == 1 && processes && !processes->processors.empty()) {
        const auto count = processes->processors.size();
        const auto columns = count <= 8 ? 4U : count <= 24 ? 6U : 8U;
        const auto rows = (count + columns - 1) / columns;
        const auto cw = (plot.right - plot.left) / columns;
        const auto ch = (plot.bottom - plot.top) / static_cast<float>(rows);
        for (std::size_t i = 0; i < count; ++i) {
            const auto& cpu = processes->processors[i];
            const float cx = plot.left + static_cast<float>(i % columns) * cw;
            const float cy = plot.top + static_cast<float>(i / columns) * ch;
            const auto label = L"CPU " + std::to_wstring(i) + (cpu.cpuPercent.valid() && !processStale ? L" · " + decimal(cpu.cpuPercent.value, L" %", 0) : L" · —");
            text(label, rect(cx, cy, cw - 7, 13), 8, muted);
            graph(history.find(L"cpu:" + std::to_wstring(cpu.group) + L":" + std::to_wstring(cpu.index)),
                rect(cx, cy + 15, cw - 7, std::max(5.f, ch - 22)), color, 100, true);
        }
    } else if (item->kind == ResourceKind::cpu && cpuView == 2 && processes) {
        graph(nullptr, plot, color, 100, true);
        for (std::size_t i = 0; i < processes->processors.size(); ++i) {
            const auto& cpu = processes->processors[i];
            const auto shade = D2D1::ColorF(.45f + .12f * static_cast<float>(i % 3), .62f + .07f * static_cast<float>(i % 4), .69f + .05f * static_cast<float>(i % 5), .65f);
            graph(history.find(L"cpu:" + std::to_wstring(cpu.group) + L":" + std::to_wstring(cpu.index)), plot, shade, 100, false, false);
        }
    } else {
        graph(series, plot, color, maximum, true);
        if (item->kind == ResourceKind::network) graph(history.find(item->id + L":up"), plot, resourceColor(ResourceKind::cpu), maximum, false);
    }
    text(rangeSeconds == 60 ? L"Před 60 sekundami" : L"Před 10 minutami", rect(plot.left, plot.bottom + 8, 200, 17), 9, muted);
    text(L"Nyní", rect(plot.right - 32, plot.bottom + 8, 32, 17), 9, muted);
    if (item->kind == ResourceKind::memory && processes) {
        const auto& memory = processes->memory;
        const bool pagingValid = memory.pageFileState == SampleState::valid && !processStale;
        const auto paging = pagingValid ? memory.pageFileBytes ? decimal(memory.pageFileUsedBytes / 1073741824., L" / ") + decimal(memory.pageFileBytes / 1073741824., L" GiB") : L"Vypnuto" : L"Měření nedostupné";
        text(L"Stránkovací soubor (swap)", rect(x + 22, plot.bottom + 34, 250, 18), 10, muted);
        text(paging, rect(x + w - 180, plot.bottom + 34, 158, 18), 10, color);
        graph(history.find(L"pagefile"), rect(x + 22, plot.bottom + 57, w - 44, 47), resourceColor(ResourceKind::network),
            std::max(1., static_cast<double>(memory.pageFileBytes)), true);
        const auto bar = rect(x + 22, y + h - 44, w - 44, 7);
        fill(bar, line, 3);
        if (item->primary.valid() && item->capacity > 0) fill(rect(bar.left, bar.top, (w - 44) * static_cast<float>(item->primary.value / item->capacity), 7), color, 3);
        text(L"▪ Využitá " + (item->primary.valid() ? decimal(item->primary.value / 1073741824., L" GiB") : L"—"), rect(x + 22, y + h - 25, 200, 18), 9, color);
        text(L"▪ Dostupná " + (item->secondary.valid() ? decimal(item->secondary.value / 1073741824., L" GiB") : L"—"), rect(x + 210, y + h - 25, 200, 18), 9, muted);
    } else if (item->kind == ResourceKind::network) {
        text(L"↓ Příjem " + (item->primary.valid() ? decimal(item->primary.value / 1000000, L" Mbit/s") : L"—"), rect(x + 22, y + h - 40, 220, 22), 11, color);
        text(L"↑ Odesílání " + (item->secondary.valid() ? decimal(item->secondary.value / 1000000, L" Mbit/s") : L"—"), rect(x + w / 2, y + h - 40, w / 2 - 22, 22), 11, resourceColor(ResourceKind::cpu));
    } else if (series && series->size()) {
        text(L"● Živá data · uchováno " + decimal(std::chrono::duration<double>(series->at(series->size() - 1).time - series->at(0).time).count(), L" s")
            + L" · " + std::to_wstring(series->size()) + L" vzorků", rect(x + 22, y + h - 40, w - 44, 22), 9, muted);
    }

    text(L"SESTAVA", rect(fx + 23, factsY + 23, factsWidth - 46, 18), 9, muted, 600, 1.6f);
    const auto model = item->kind == ResourceKind::memory ? decimal((processes && processes->memory.installedBytes ? static_cast<double>(processes->memory.installedBytes) : item->capacity) / 1073741824., L" GiB") : item->description;
    const auto modelSize = text(model, rect(fx + 23, factsY + 50, factsWidth - 46, 90), 17, ink, 600);
    std::vector<std::pair<std::wstring, std::wstring>> facts;
    std::vector<std::pair<std::wstring, std::wstring>> stats;
    const auto measured = [&](Metric<double> value, double scaleBy, const wchar_t* suffix) { return value.valid() ? decimal(value.value / scaleBy, suffix) : L"Nezměřeno"; };
    if (item->kind == ResourceKind::cpu && processes) {
        facts = {{L"Logické procesory", std::to_wstring(processes->logicalProcessors)}, {L"Skupiny CPU", processes->processors.empty() ? L"Nezměřeno" : std::to_wstring(processes->processors.back().group + 1)}, {L"Obnova", interval.load() == 1000 ? L"1 s" : L"250 ms"}};
        std::uint64_t threads = 0, handles = 0;
        for (const auto& process : processes->processes) { threads += process.counters.threads; handles += process.counters.handles; }
        stats = {{L"Procesy", std::to_wstring(processes->processes.size())}, {L"Vlákna", std::to_wstring(threads)}, {L"Popisovače", std::to_wstring(handles)}, {L"Sběr", decimal(std::chrono::duration<double, std::milli>(processes->collectionTime).count(), L" ms")}};
    } else if (item->kind == ResourceKind::memory && processes) {
        const auto& memory = processes->memory;
        facts = {{L"Dostupné systému", decimal(memory.physicalBytes / 1073741824., L" GiB")}, {L"Stránkovací soubor", memory.pageFileState == SampleState::valid ? decimal(memory.pageFileBytes / 1073741824., L" GiB") : L"Nezměřeno"},
            {L"Maximum swapu", memory.pageFileState == SampleState::valid ? decimal(memory.pageFilePeakBytes / 1073741824., L" GiB") : L"Nezměřeno"}};
        if (item->primary.valid()) stats = {{L"Dostupná", measured(item->secondary, 1073741824., L" GiB")}, {L"Potvrzeno", decimal(memory.committedBytes / 1073741824., L" GiB")},
            {L"Stránkovaný fond", decimal(memory.pagedPoolBytes / 1073741824., L" GiB")}, {L"Nestránkovaný fond", decimal(memory.nonpagedPoolBytes / 1073741824., L" GiB")},
            {L"Swap využitý", memory.pageFileState == SampleState::valid ? decimal(memory.pageFileUsedBytes / 1073741824., L" GiB") : L"Nezměřeno"}, {L"Limit potvrzení", decimal(memory.commitLimitBytes / 1073741824., L" GiB")}};
    } else if (item->kind == ResourceKind::disk) {
        facts = {{L"Zařízení", item->title}, {L"Zdroj", L"PhysicalDisk"}};
        stats = {{L"Čtení", measured(item->secondary, 1048576, L" MiB/s")}, {L"Zápis", measured(item->tertiary, 1048576, L" MiB/s")}, {L"Aktivní čas", measured(item->primary, 1, L" %")}};
    } else if (item->kind == ResourceKind::network) {
        facts = {{L"Připojení", item->title}, {L"Rychlost linky", decimal(item->capacity / 1000000000, L" Gbit/s")}, {L"Stav", item->status}};
        stats = {{L"↓ Příjem", measured(item->primary, 1000000, L" Mbit/s")}, {L"↑ Odesílání", measured(item->secondary, 1000000, L" Mbit/s")}};
    } else {
        facts = {{L"Adaptér", item->title}, {L"Vyhrazená paměť", decimal(item->capacity / 1073741824., L" GiB")}, {L"Zobrazené využití", L"Nejvyšší engine"}};
        stats = {{L"Aktivita", measured(item->primary, 1, L" %")}, {L"Zdroj", L"GPU Engine"}};
    }
    const auto factsColumnWidth = frame.stacked ? (factsWidth - 66) / 2 : factsWidth - 46;
    float fy = frame.stacked ? factsY + 98 : factsY + 50 + modelSize.height + 20;
    const auto statisticsTop = fy;
    for (const auto& [label, value] : facts) {
        text(label, rect(fx + 23, fy, factsColumnWidth * .53f, 18), 10, muted);
        text(value, rect(fx + 23 + factsColumnWidth * .53f, fy, factsColumnWidth * .47f, 20), 10, ink, 400, 0, DWRITE_TEXT_ALIGNMENT_TRAILING);
        fy += 27;
    }
    if (!frame.stacked) { fill(rect(fx + 23, fy + 8, factsWidth - 46, 1), line); fy += 32; }
    else fy = statisticsTop;
    for (std::size_t i = 0; i < stats.size(); ++i) {
        const auto columnWidth = (factsColumnWidth - 15) / 2;
        const auto sx = fx + 23 + (frame.stacked ? factsColumnWidth + 20 : 0) + static_cast<float>(i % 2) * (columnWidth + 15);
        const auto sy = fy + static_cast<float>(i / 2) * 57;
        if (sy + 40 > factsY + frame.factsHeight - 12) break;
        text(stats[i].first, rect(sx, sy, columnWidth, 17), 9, muted);
        text(stats[i].second, rect(sx, sy + 19, columnWidth, 24), 14, ink, 600);
    }
    const auto rowY = frame.rowY;
    text(L"S O U V I S L O S T I", rect(x, rowY - 49, 250, 18), 9, muted);
    text(item->kind == ResourceKind::memory ? L"Procesy podle pracovní sady" : L"Procesy podle CPU · celý systém", rect(x, rowY - 33, available - 180, 27), 16, ink, 600);
    surface->SetTransform(D2D1::Matrix3x2F::Identity());
    surface->PopAxisAlignedClip();
    if (frame.maxScroll > 0) {
        const auto viewport = frame.bottom - frame.top;
        const auto thumb = std::max(30.f, viewport * viewport / frame.contentHeight);
        fill(rect(frame.detailRight - 10, frame.top + detailScroll / frame.maxScroll * (viewport - thumb), 6, thumb), line, 3);
    }
}

void Desktop::updateConsumers()
{
    consumers.fill(std::nullopt);
    if (!processes || processStale) return;
    const auto* resource = selectedResource();
    const bool memory = resource && resource->kind == ResourceKind::memory;
    std::vector<const ProcessRecord*> candidates;
    for (const auto& process : processes->processes)
        if (process.identity.pid != 0 && (memory || process.cpuPercent.valid())) candidates.push_back(&process);
    const auto count = std::min<std::size_t>(3, candidates.size());
    std::partial_sort(candidates.begin(), candidates.begin() + count, candidates.end(), [&](auto* a, auto* b) {
        const double av = memory ? static_cast<double>(a->counters.workingSetBytes) : a->cpuPercent.value;
        const double bv = memory ? static_cast<double>(b->counters.workingSetBytes) : b->cpuPercent.value;
        return av == bv ? a->identity.pid < b->identity.pid : av > bv;
    });
    for (std::size_t i = 0; i < count; ++i) consumers[i] = candidates[i]->identity;
    for (auto control : consumerButtons) InvalidateRect(control, nullptr, FALSE);
}

}
