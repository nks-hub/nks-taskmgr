#include "core/history_stream.hpp"
#include <algorithm>
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
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "FAIL disk history: " << error.what() << '\n'; return 1;
    }
}
