#pragma once
#include "models.hpp"
#include "ui.hpp"

namespace nks::candidate {
media::PathGeometry geometry(const History* history, double width, double height, int64_t now, int seconds, double maximum);
void draw(shapes::Path const& path, const History* history, double width, double height, int64_t now, int seconds, double maximum);
uint32_t resourceColor(uint32_t kind);
}
