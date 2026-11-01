#include "models.hpp"
#include <Windows.h>
#include <cwctype>
#include <iomanip>
#include <sstream>
#include <stdexcept>

namespace nks::candidate {
std::wstring ProcessKey::text() const {
    std::wostringstream out;
    out << std::hex << bootLow << L':' << bootHigh << L':' << pid << L':' << kind << L':' << generation;
    return out.str();
}
std::wstring copy(NksString16 text) {
    if (text.length > 1048576 || (text.length != 0 && !text.data))
        throw std::runtime_error("Invalid counted native string");
    return text.length ? std::wstring(text.data, text.length) : std::wstring{};
}
std::wstring number(double value, const wchar_t* unit, int digits) {
    if (!std::isfinite(value)) return L"Měření nedostupné";
    std::wostringstream out;
    out << std::fixed << std::setprecision(digits) << value << unit;
    auto text = out.str();
    std::replace(text.begin(), text.end(), L'.', L',');
    return text;
}
bool valid(NksMetric value) {
    return value.state == NKS_SAMPLE_VALID && std::isfinite(value.value) && value.value >= 0;
}
std::wstring sampleState(uint32_t state) {
    switch (state) {
    case NKS_SAMPLE_VALID: return L"Platné";
    case NKS_SAMPLE_FIRST: return L"První vzorek";
    case NKS_SAMPLE_ACCESS_DENIED: return L"Chybí oprávnění";
    case NKS_SAMPLE_UNSUPPORTED: return L"Nepodporováno";
    case NKS_SAMPLE_STALE: return L"Zastaralá data";
    case NKS_SAMPLE_GAP: return L"Mezera";
    case NKS_SAMPLE_ERROR: return L"Chyba měření";
    default: return L"Měření nedostupné";
    }
}
std::wstring metric(NksMetric value, const wchar_t* unit, double divisor) {
    return valid(value) ? number(value.value / divisor, unit)
        : sampleState(value.state == NKS_SAMPLE_VALID ? NKS_SAMPLE_ERROR : value.state);
}
std::wstring lower(std::wstring text) {
    if (text.empty()) return text;
    std::wstring result(text.size(), L'\0');
    const auto length = LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE, text.data(), static_cast<int>(text.size()),
        result.data(), static_cast<int>(result.size()), nullptr, nullptr, 0);
    if (length > 0) { result.resize(static_cast<size_t>(length)); return result; }
    return text;
}
void History::append(int64_t time, NksMetric value) {
    if (time <= 0 || (!points.empty() && time <= points.back().time)) return;
    if (value.state == NKS_SAMPLE_VALID && !valid(value)) value.state = NKS_SAMPLE_ERROR;
    points.push_back({time, value, interrupted});
    interrupted = false;
    while (points.size() > 2401) points.pop_front();
}
double History::maximum(int64_t start, double floor) const {
    for (const auto& point : points)
        if (point.time >= start && valid(point.metric)) floor = std::max(floor, point.metric.value);
    return floor;
}
std::wstring resourceValue(const Resource& resource, bool stale) {
    if (stale) return L"Zastaralá data";
    if (resource.kind == NKS_RESOURCE_NETWORK)
        return L"↓ " + metric(resource.primary, L"", 1e6) + L"  ↑ " + metric(resource.secondary, L" Mbit/s", 1e6);
    return metric(resource.primary, resource.kind == NKS_RESOURCE_MEMORY ? L" GiB" : L" %",
        resource.kind == NKS_RESOURCE_MEMORY ? 1073741824.0 : 1.0);
}
std::wstring serviceState(uint32_t state) {
    switch (state) {
    case 1: return L"Zastavena";
    case 2: return L"Spouštění";
    case 3: return L"Zastavování";
    case 4: return L"Spuštěna";
    case 5: return L"Obnovování";
    case 6: return L"Pozastavování";
    case 7: return L"Pozastavena";
    default: return L"Neznámý stav";
    }
}
}
