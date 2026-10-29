#pragma once

#include <algorithm>

namespace nks {

struct ServiceLayout {
    float left{}, width{}, overviewTop{150.59375f}, overviewHeight{84}, workspaceTop{}, bottom{};
    float inventoryWidth{}, detailX{}, detailWidth{}, tableTop{}, tableHeight{};
    float searchX{}, searchWidth{}, searchY{84};
    bool overview{}, stacked{}, compactDetail{};

    static ServiceLayout calculate(float width, float height) {
        ServiceLayout frame;
        const auto sidebar = width <= 1000 ? 68.f : 88.f;
        const auto padding = width <= 780 ? 15.f : width <= 1000 ? 20.f : 30.f;
        frame.left = sidebar + padding; frame.width = width - frame.left - padding;
        frame.overview = height >= 650; frame.stacked = width <= 850 && height >= 550;
        frame.compactDetail = !frame.stacked && height < 650;
        frame.workspaceTop = frame.overview ? frame.overviewTop + frame.overviewHeight + 20 : frame.overviewTop;
        frame.bottom = height - 52;
        frame.detailWidth = frame.stacked ? frame.width : width <= 850 ? 245.f : width <= 1250 ? 300.f : 330.f;
        frame.inventoryWidth = frame.stacked ? frame.width : frame.width - frame.detailWidth;
        frame.detailX = frame.stacked ? frame.left : frame.left + frame.inventoryWidth;
        frame.tableTop = frame.workspaceTop + 71;
        frame.tableHeight = frame.stacked ? std::max(110.f, (frame.bottom - frame.tableTop) * .43f) : frame.bottom - frame.tableTop;
        frame.searchWidth = width <= 1000 ? 190.f : 235.f;
        frame.searchX = width - (width <= 1000 ? 22.f : 30.f) - 179 - frame.searchWidth;
        return frame;
    }
};

}
