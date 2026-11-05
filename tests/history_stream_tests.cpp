#include "core/history_stream.hpp"
#include <algorithm>
#include <chrono>
#include <string>
#include <vector>
#include <iostream>
#include <stdexcept>

namespace {
void require(bool value, const char* message)
{
    if (!value) throw std::runtime_error(message);
}

nks::ResourceSnapshot diskSample(std::int64_t seconds)
{
    nks::ResourceSnapshot sample;
    sample.measuredAt = std::chrono::steady_clock::time_point(std::chrono::seconds(seconds));
    nks::ResourceRecord disk;
    disk.id = L"disk-0"; disk.kind = nks::ResourceKind::disk;
    disk.primary = {25, nks::SampleState::valid, nks::MetricSource::performanceCounter};
    disk.secondary = {4096, nks::SampleState::valid, nks::MetricSource::performanceCounter};
    disk.tertiary = {8192, nks::SampleState::valid, nks::MetricSource::performanceCounter};
    sample.devices.push_back(disk);
    return sample;
}

const nks::CollectedHistoryPoint& point(const nks::HistoryBatch& batch, nks::HistoryChannel channel)
{
    const auto definition = std::find_if(batch.definitions.begin(), batch.definitions.end(),
        [channel](const auto& entry) { return entry.resourceId == L"disk-0" && entry.channel == channel; });
    require(definition != batch.definitions.end(), "Disk history definition is absent");
    const auto found = std::find_if(batch.points.begin(), batch.points.end(),
        [&](const auto& entry) { return entry.series == definition->id; });
    require(found != batch.points.end(), "Disk history point is absent");
    return *found;
}

nks::ResourceSnapshot gpuSample(std::int64_t seconds, std::size_t engines)
{
    nks::ResourceSnapshot sample;
    sample.measuredAt = std::chrono::steady_clock::time_point(std::chrono::seconds(seconds));
    nks::ResourceRecord gpu;
    gpu.id = L"gpu-0"; gpu.kind = nks::ResourceKind::gpu;
    gpu.primary = gpu.secondary = gpu.tertiary = {1, nks::SampleState::valid, nks::MetricSource::performanceCounter};
    for (std::size_t i = 0; i < engines; ++i)
        gpu.engines.push_back({L"engine-" + std::to_wstring(i), L"Engine", {double(i % 100), nks::SampleState::valid, nks::MetricSource::performanceCounter}});
    sample.devices.push_back(std::move(gpu));
    return sample;
}

// Overfills the ring without a reader: the oldest points go, the loss is counted exactly once, and the newest
// points, including a one-sample spike, arrive in order and in batches no larger than the limit.
void overflowKeepsNewest()
{
    nks::HistoryStream history;
    const std::size_t samples = nks::HistoryStream::capacity / 3 + 1000, spikeAt = samples - 10;
    for (std::size_t i = 0; i < samples; ++i) {
        auto sample = diskSample(static_cast<std::int64_t>(i) + 1);
        if (i == spikeAt) sample.devices.front().primary.value = 100;
        history.append(sample);
    }
    std::uint64_t lost{}, seen{}, last{}, spikes{};
    while (auto batch = history.take()) {
        require(batch->points.size() <= nks::HistoryStream::maximumBatch, "History batch exceeds its limit");
        lost += batch->lostPoints;
        for (const auto& item : batch->points) {
            require(last == 0 || item.sequence == last + 1, "Retained history points are not contiguous");
            last = item.sequence; ++seen;
            if (item.metric.value == 100) {
                ++spikes;
                require(item.timeNs == static_cast<std::int64_t>(spikeAt + 1) * 1'000'000'000, "The spike moved in time");
            }
        }
    }
    require(lost == samples * 3 - nks::HistoryStream::capacity && seen == nks::HistoryStream::capacity && last == samples * 3,
        "Overflow did not keep exactly the newest points and count the rest as lost");
    require(spikes == 1, "A one-sample spike was lost or duplicated");
    std::cout << "PASS history overflow: " << samples * 3 << " points, " << lost << " lost, " << seen << " newest kept in order, spike kept\n";
}

void seriesLimitAndCost()
{
    nks::HistoryStream history;
    history.append(gpuSample(1, 600));
    auto batch = history.take();
    require(batch && batch->definitions.size() == nks::HistoryStream::maximumSeries
        && batch->lostPoints == 3 + 600 - nks::HistoryStream::maximumSeries, "Series beyond the limit were not refused and counted");
    while (history.take()) {}
    constexpr int seconds = 600;
    std::vector<nks::ResourceSnapshot> samples;
    for (int i = 2; i < seconds + 2; ++i) samples.push_back(gpuSample(i, 509));
    const auto started = std::chrono::steady_clock::now();
    std::size_t points{};
    for (const auto& sample : samples) {
        history.append(sample);
        while (auto next = history.take()) points += next->points.size();
    }
    const auto perSample = std::chrono::duration<double, std::micro>(std::chrono::steady_clock::now() - started).count() / seconds;
    require(points == seconds * nks::HistoryStream::maximumSeries, "The full series set lost points with a reader present");
    require(perSample < 2000, "Appending and taking one full 512-series sample costs 2 ms or more");
    std::cout << "PASS history series limit: 512 kept, 91 refused; one full 512-series sample append+take " << perSample << " us\n";
}
}

int main()
{
    try {
        nks::HistoryStream history;
        auto sample = diskSample(1); history.append(sample);
        auto batch = history.take();
        require(batch && batch->points.size() == 3, "Disk activity/read/write were not captured together");
        const auto& read = point(*batch, nks::HistoryChannel::diskRead);
        const auto& write = point(*batch, nks::HistoryChannel::diskWrite);
        require(read.metric.valid() && read.metric.value == 4096 && write.metric.valid() && write.metric.value == 8192,
            "Disk transfer history substituted activity or merged read and write");
        require(read.timeNs == 1'000'000'000 && write.timeNs == read.timeNs, "Disk transfer histories use different clocks");

        sample = diskSample(2); sample.devices.front().secondary = {0, nks::SampleState::error, nks::MetricSource::performanceCounter};
        sample.devices.front().tertiary.value = 0; history.append(sample); batch = history.take();
        require(point(*batch, nks::HistoryChannel::diskRead).metric.state == nks::SampleState::error,
            "Unavailable disk read became a valid zero");
        require(point(*batch, nks::HistoryChannel::diskWrite).metric.valid()
            && point(*batch, nks::HistoryChannel::diskWrite).metric.value == 0, "Measured zero disk write became missing data");

        history.appendGap(false, std::chrono::steady_clock::time_point(std::chrono::seconds(3))); batch = history.take();
        require(point(*batch, nks::HistoryChannel::diskRead).metric.state == nks::SampleState::gap
            && point(*batch, nks::HistoryChannel::diskWrite).metric.state == nks::SampleState::gap,
            "Collector failure did not mark both disk transfer histories");

        sample = diskSample(4); sample.devices.clear(); history.append(sample); batch = history.take();
        require(point(*batch, nks::HistoryChannel::diskRead).metric.state == nks::SampleState::unavailable
            && point(*batch, nks::HistoryChannel::diskWrite).metric.state == nks::SampleState::unavailable,
            "Removed disk kept an apparently current transfer history");
        std::cout << "PASS disk history: independent transfers, native timestamps, zero/error, failure and removal\n";
        overflowKeepsNewest();
        seriesLimitAndCost();
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "FAIL disk history: " << error.what() << '\n'; return 1;
    }
}
