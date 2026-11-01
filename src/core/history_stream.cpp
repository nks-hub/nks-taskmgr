#include "history_stream.hpp"
#include <algorithm>
#include <chrono>

namespace nks {
namespace {
std::int64_t nanoseconds(std::chrono::steady_clock::time_point value)
{
    return std::chrono::duration_cast<std::chrono::nanoseconds>(value.time_since_epoch()).count();
}
}

std::size_t HistoryStream::KeyHash::operator()(const Key& key) const noexcept
{
    auto result = std::hash<std::wstring>{}(key.resource);
    const auto mix = [&](std::size_t value) { result ^= value + 0x9e3779b9 + (result << 6) + (result >> 2); };
    mix(std::hash<std::wstring>{}(key.detail));
    mix(static_cast<std::size_t>(key.channel)); mix(key.group); mix(key.index);
    return result;
}

void HistoryStream::push(Entry& entry, std::int64_t time, Metric<double> metric)
{
    if (time <= entry.lastTime) return;
    if (!points) points = std::make_unique<CollectedHistoryPoint[]>(capacity);
    if (length == capacity) { begin = (begin + 1) % capacity; --length; ++lost; }
    points[(begin + length) % capacity] = {nextSequence++, time, entry.definition.id, 0, metric};
    ++length; entry.lastTime = time;
}

void HistoryStream::append(HistoryChannel channel, const std::wstring& resource, const std::wstring& detail,
    const std::wstring& name, std::uint32_t group, std::uint32_t index, std::int64_t time, Metric<double> metric)
{
    if (resource.size() > 1024 || detail.size() > 1024 || name.size() > 1024) { ++lost; return; }
    Key key{channel, group, index, resource, detail};
    auto found = indexes.find(key);
    if (found == indexes.end()) {
        const auto bytes = sizeof(Entry) + sizeof(Key) + 128
            + sizeof(wchar_t) * (2 * resource.size() + 2 * detail.size() + name.size() + 5);
        if (entries.size() == maximumSeries || bytes > definitionBudget - definitionBytes) { ++lost; return; }
        Entry entry{{static_cast<std::uint32_t>(entries.size() + 1), channel, group, index, resource, detail, name}, 0};
        entries.push_back(std::move(entry));
        try { found = indexes.emplace(std::move(key), entries.size() - 1).first; }
        catch (...) { entries.pop_back(); throw; }
        definitionBytes += bytes;
    }
    push(entries[found->second], time, metric);
}

void HistoryStream::append(const ProcessSnapshot& snapshot)
{
    const auto time = nanoseconds(snapshot.measuredAt);
    std::lock_guard lock(mutex);
    append(HistoryChannel::primary, L"cpu", L"", L"", 0, 0, time, snapshot.systemCpuPercent);
    const auto& memory = snapshot.memory;
    const Metric<double> used{static_cast<double>(memory.physicalBytes - std::min(memory.physicalBytes, memory.availableBytes)),
        memory.state, MetricSource::performanceInfo};
    append(HistoryChannel::primary, L"memory", L"", L"", 0, 0, time, used);
    append(HistoryChannel::pageFile, L"memory", L"", L"", 0, 0, time,
        {static_cast<double>(memory.pageFileUsedBytes), memory.pageFileState, MetricSource::performanceInfo});
    for (const auto& processor : snapshot.processors)
        append(HistoryChannel::processor, L"cpu", L"", L"", processor.group, processor.index, time, processor.cpuPercent);
    for (auto& entry : entries) {
        if (entry.definition.channel != HistoryChannel::processor || entry.lastTime == time) continue;
        push(entry, time, {0, SampleState::unavailable, MetricSource::processorTime});
    }
}

void HistoryStream::append(const ResourceSnapshot& snapshot)
{
    const auto time = nanoseconds(snapshot.measuredAt);
    std::lock_guard lock(mutex);
    for (const auto& resource : snapshot.devices) {
        append(HistoryChannel::primary, resource.id, L"", L"", 0, 0, time, resource.primary);
        if (resource.kind == ResourceKind::network)
            append(HistoryChannel::upload, resource.id, L"", L"", 0, 0, time, resource.secondary);
        if (resource.kind != ResourceKind::gpu) continue;
        append(HistoryChannel::dedicated, resource.id, L"", L"", 0, 0, time, resource.secondary);
        append(HistoryChannel::shared, resource.id, L"", L"", 0, 0, time, resource.tertiary);
        for (const auto& engine : resource.engines)
            append(HistoryChannel::engine, resource.id, engine.id, engine.name, 0, 0, time, engine.utilization);
    }
    for (auto& entry : entries) {
        const auto& definition = entry.definition;
        if (definition.resourceId == L"cpu" || definition.resourceId == L"memory" || entry.lastTime == time) continue;
        push(entry, time, {0, SampleState::unavailable, MetricSource::performanceCounter});
    }
}

void HistoryStream::appendGap(bool processes, std::chrono::steady_clock::time_point measured)
{
    const auto time = nanoseconds(measured);
    std::lock_guard lock(mutex);
    for (auto& entry : entries) {
        const bool process = entry.definition.resourceId == L"cpu" || entry.definition.resourceId == L"memory";
        if (process == processes) push(entry, time, {0, SampleState::gap, MetricSource::nativeProcess});
    }
}

std::unique_ptr<HistoryBatch> HistoryStream::take()
{
    std::lock_guard lock(mutex);
    if (length == 0 && lost == 0 && errors.load(std::memory_order_relaxed) == 0) return {};
    auto batch = std::make_unique<HistoryBatch>();
    batch->definitions.reserve(entries.size());
    for (const auto& entry : entries) batch->definitions.push_back(entry.definition);
    const auto count = std::min(length, maximumBatch);
    batch->points.reserve(count);
    for (std::size_t i = 0; i < count; ++i) batch->points.push_back(points[(begin + i) % capacity]);
    batch->lostPoints = lost;
    batch->captureErrors = errors.exchange(0, std::memory_order_relaxed);
    begin = (begin + count) % capacity; length -= count; lost = 0;
    batch->morePending = length != 0;
    return batch;
}

}
