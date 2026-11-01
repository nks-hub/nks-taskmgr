#include "charts.hpp"

namespace nks::candidate {
uint32_t resourceColor(uint32_t kind) {
    constexpr uint32_t colors[]{0x80c5c0, 0xabb0f1, 0x83bca0, 0xcc9bb6, 0xdfaf76};
    return kind < std::size(colors) ? colors[kind] : colors[0];
}
media::PathGeometry geometry(const History* history, double width, double height, int64_t now, int seconds, double maximum) {
    media::PathGeometry result;
    if (!history || width <= 0 || height <= 0 || maximum <= 0) return result;
    const auto start = now - static_cast<int64_t>(seconds) * 1000000000;
    media::PathFigure figure{nullptr};
    int64_t previous{};
    for (const auto& point : history->points) {
        if (point.time < start || point.time > now) continue;
        if (!valid(point.metric)) { figure = nullptr; previous = 0; continue; }
        const auto x = static_cast<float>(static_cast<double>(point.time - start) / (static_cast<double>(seconds) * 1e9) * width);
        const auto y = static_cast<float>(height * (1 - std::clamp(point.metric.value / maximum, 0.0, 1.0)));
        if (!figure || point.breakBefore || (previous && point.time - previous > 2500000000LL)) {
            figure = media::PathFigure(); figure.StartPoint({x, y}); figure.IsFilled(false); figure.IsClosed(false);
            result.Figures().Append(figure);
        } else {
            media::LineSegment segment; segment.Point({x, y}); figure.Segments().Append(segment);
        }
        previous = point.time;
    }
    return result;
}
void draw(shapes::Path const& path, const History* history, double width, double height, int64_t now, int seconds, double maximum) {
    path.Data(geometry(history, width, height, now, seconds, maximum));
}
}
