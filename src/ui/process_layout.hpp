#pragma once

#include <algorithm>

namespace nks {

struct ProcessLayout {
    float left{}, width{}, overviewTop{150.59375f}, overviewHeight{108};
    float toolbarTop{}, tableTop{}, tableHeight{}, dockTop{}, dockHeight{92};
    float searchX{}, searchWidth{}, searchY{84};
    bool overview{}, compact{};

    static ProcessLayout calculate(float width, float height) {
        ProcessLayout frame;
        frame.compact = width <= 780;
        const auto sidebar = width <= 1000 ? 68.f : 88.f;
        const auto padding = frame.compact ? 15.f : width <= 1000 ? 20.f : 30.f;
        frame.left = sidebar + padding;
        frame.width = width - frame.left - padding;
        frame.overview = height >= 650;
        frame.overviewHeight = frame.compact ? 90.f : 108.f;
        frame.toolbarTop = frame.overview ? frame.overviewTop + frame.overviewHeight + 22 : frame.overviewTop;
        frame.tableTop = frame.toolbarTop + 51;
        frame.dockTop = height - 52 - frame.dockHeight;
        frame.tableHeight = std::max(80.f, frame.dockTop - frame.tableTop);
        frame.searchWidth = width <= 1000 ? 190.f : 235.f;
        frame.searchX = width - (width <= 1000 ? 22.f : 30.f) - 179 - frame.searchWidth;
        return frame;
    }
};

}
