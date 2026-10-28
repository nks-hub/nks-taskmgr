#pragma once
#include <d2d1.h>

namespace nks::theme {
inline const auto ink = D2D1::ColorF(0xe9eef0);
inline const auto muted = D2D1::ColorF(0x95a4ad);
inline const auto panel = D2D1::ColorF(0x1d252a);
inline const auto line = D2D1::ColorF(0x2c363d);
inline const auto amber = D2D1::ColorF(0xedc08b);
inline D2D1_RECT_F rect(float x, float y, float w, float h) { return D2D1::RectF(x, y, x + w, y + h); }
}
