#pragma once

#include "resource.hpp"
#include <array>
#include <map>
#include <span>

namespace nks {

struct HistoryPoint {
    std::chrono::steady_clock::time_point time;
    Metric<double> metric;
};

class HistorySeries {
public:
    static constexpr std::size_t capacity = 2401;
    void append(HistoryPoint point) {
        if (length && point.time <= at(length - 1).time) return;
        points[(begin + length) % capacity] = point;
        if (length == capacity) begin = (begin + 1) % capacity;
        else ++length;
    }
    const HistoryPoint& at(std::size_t index) const { return points[(begin + index) % capacity]; }
    std::size_t size() const { return length; }
private:
    std::array<HistoryPoint, capacity> points{};
    std::size_t begin{}, length{};
};

class ResourceHistory {
public:
    static constexpr std::size_t maximumSeries = 512;
    void append(const std::wstring& id, HistoryPoint point) {
        if (!series.contains(id) && series.size() == maximumSeries) {
            auto oldest = series.begin();
            for (auto entry = series.begin(); entry != series.end(); ++entry)
                if (entry->second.at(entry->second.size() - 1).time < oldest->second.at(oldest->second.size() - 1).time)
                    oldest = entry;
            series.erase(oldest);
        }
        series[id].append(point);
    }
    const HistorySeries* find(const std::wstring& id) const {
        const auto match = series.find(id);
        return match == series.end() ? nullptr : &match->second;
    }
    std::size_t size() const { return series.size(); }
    void appendGpu(const ResourceRecord& gpu, std::chrono::steady_clock::time_point time) {
        append(gpu.id + L":dedicated", {time, gpu.secondary});
        append(gpu.id + L":shared", {time, gpu.tertiary});
        const auto prefix = gpu.id + L"_phys_";
        for (auto& [id, history] : series) {
            if (!id.starts_with(prefix)) continue;
            bool found = false;
            for (const auto& engine : gpu.engines) if (id == gpu.id + engine.id) { found = true; break; }
            if (!found) history.append({time, {0, SampleState::unavailable, MetricSource::performanceCounter}});
        }
        for (const auto& engine : gpu.engines) append(gpu.id + engine.id, {time, engine.utilization});
    }
private:
    static_assert(sizeof(HistorySeries) * maximumSeries < 64 * 1024 * 1024);
    std::map<std::wstring, HistorySeries> series;
};

}
