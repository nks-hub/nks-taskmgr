#pragma once

#include "core/resource.hpp"
#include <algorithm>

namespace nks {

struct PerformanceLayout {
    float sidebar{}, padding{}, inventoryX{}, inventoryWidth{}, detailX{}, detailRight{};
    float top{150.59375f}, bottom{}, available{}, chartWidth{}, chartHeight{475};
    float factsX{}, factsY{}, factsWidth{}, factsHeight{};
    float rowY{}, consumerGap{}, contentHeight{}, maxScroll{};
    bool stacked{}, compact{};

    static PerformanceLayout calculate(float width, float height, ResourceKind kind, std::size_t engines = 0) {
        PerformanceLayout frame;
        frame.sidebar = width <= 1000 ? 68.f : 88.f;
        frame.padding = width <= 780 ? 15.f : width <= 1000 ? 20.f : 25.f;
        frame.compact = width <= 780;
        frame.inventoryWidth = frame.compact ? 180.f : 278.f;
        frame.inventoryX = frame.sidebar + frame.padding;
        frame.detailX = frame.inventoryX + frame.inventoryWidth + (frame.compact ? 12.f : 20.f);
        frame.detailRight = width - frame.padding;
        frame.bottom = std::max(frame.top + 1, height - 52);
        frame.stacked = width <= 1250;
        if (kind == ResourceKind::gpu) frame.chartHeight = 360 + static_cast<float>(std::max<std::size_t>(2, (engines + 1) / 2)) * 130;
        frame.factsHeight = !frame.stacked ? std::min(frame.chartHeight, 475.f) : kind == ResourceKind::memory ? 264.f : kind == ResourceKind::gpu ? 264.f
            : kind == ResourceKind::cpu || kind == ResourceKind::disk ? 207.f : 166.f;
        const auto workspace = frame.chartHeight + (frame.stacked ? frame.factsHeight + 14 : 0);
        frame.contentHeight = workspace + (kind == ResourceKind::cpu || kind == ResourceKind::memory ? 148.f : 0.f);
        frame.maxScroll = std::max(0.f, frame.contentHeight - (frame.bottom - frame.top));
        frame.available = frame.detailRight - frame.detailX - (frame.maxScroll > 0 ? 17.f : 0);
        frame.factsWidth = frame.stacked ? frame.available : 242.f;
        frame.chartWidth = frame.stacked ? frame.available : frame.available - frame.factsWidth - 14;
        frame.factsX = frame.stacked ? frame.detailX : frame.detailX + frame.chartWidth + 14;
        frame.factsY = frame.stacked ? frame.top + frame.chartHeight + 14 : frame.top;
        frame.rowY = frame.top + workspace + 74;
        frame.consumerGap = width <= 1000 ? 8.f : 13.f;
        return frame;
    }
};

}
