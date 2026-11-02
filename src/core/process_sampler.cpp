#include "process_sampler.hpp"

#include <algorithm>
#include <cmath>
#include <unordered_map>

namespace nks {
namespace {

struct IdentityHash {
    std::size_t operator()(const ProcessIdentity& identity) const noexcept
    {
        auto hash = std::hash<std::uint64_t>{}(identity.boot.low);
        hash ^= std::hash<std::uint64_t>{}(identity.boot.high) + 0x9e3779b9 + (hash << 6) + (hash >> 2);
        hash ^= std::hash<std::uint64_t>{}(identity.generation) + 0x9e3779b9 + (hash << 6) + (hash >> 2);
        hash ^= std::hash<std::uint32_t>{}(identity.pid) + static_cast<std::size_t>(identity.generationKind);
        return hash;
    }
};

Metric<double> rate(std::uint64_t current, std::uint64_t before, double seconds)
{
    if (current < before)
        return {0, SampleState::gap, MetricSource::nativeProcess};
    return {static_cast<double>(current - before) / seconds, SampleState::valid, MetricSource::nativeProcess};
}

}

void ProcessSampler::apply(ProcessSnapshot& snapshot)
{
    const auto observed = measurementTime(snapshot.measuredAt);
    const auto beforeObserved = previous ? measurementTime(previous->measuredAt) : 0;
    auto& metadata = snapshot.measurements;
    metadata.processCpu = measurementMetadata(MeasurementUnit::percent, MeasurementNormalization::wholeMachine,
        MeasurementBasis::elapsedDelta, observed, beforeObserved, snapshot.logicalProcessors);
    metadata.processIo = measurementMetadata(MeasurementUnit::bytesPerSecond, MeasurementNormalization::none,
        MeasurementBasis::elapsedDelta, observed, beforeObserved);
    metadata.processMemory = measurementMetadata(MeasurementUnit::bytes, MeasurementNormalization::none,
        MeasurementBasis::gauge, observed, beforeObserved);
    metadata.systemCpu = measurementMetadata(MeasurementUnit::percent, MeasurementNormalization::wholeMachine,
        MeasurementBasis::counterRatio, metadata.systemCpu.observedAtNs, previous ? previous->measurements.systemCpu.observedAtNs : 0);
    metadata.logicalCpu = measurementMetadata(MeasurementUnit::percent, MeasurementNormalization::logicalProcessor,
        MeasurementBasis::counterRatio, metadata.logicalCpu.observedAtNs, previous ? previous->measurements.logicalCpu.observedAtNs : 0);
    metadata.systemMemory = measurementMetadata(MeasurementUnit::bytes, MeasurementNormalization::none,
        MeasurementBasis::gauge, metadata.systemMemory.observedAtNs, previous ? previous->measurements.systemMemory.observedAtNs : 0);
    snapshot.systemCpuPercent = {0, SampleState::first, MetricSource::processorTime};
    for (auto& processor : snapshot.processors) processor.cpuPercent = {0, SampleState::first, MetricSource::processorTime};
    for (auto& process : snapshot.processes) {
        process.cpuPercent = {};
        process.readIoBytesPerSecond = {};
        process.writeIoBytesPerSecond = {};
    }
    if (!previous) {
        previous = snapshot;
        return;
    }
    const auto intervalNs = std::chrono::duration_cast<std::chrono::nanoseconds>(snapshot.measuredAt - previous->measuredAt).count();
    const auto seconds = static_cast<double>(intervalNs) / 1'000'000'000.;
    if (seconds <= 0 || seconds > 30 || snapshot.logicalProcessors == 0
        || snapshot.logicalProcessors != previous->logicalProcessors) {
        for (auto& process : snapshot.processes) {
            process.cpuPercent.state = SampleState::gap;
            process.readIoBytesPerSecond.state = SampleState::gap;
            process.writeIoBytesPerSecond.state = SampleState::gap;
        }
        snapshot.systemCpuPercent.state = SampleState::gap;
        for (auto& processor : snapshot.processors) processor.cpuPercent.state = SampleState::gap;
        previous = snapshot;
        return;
    }
    setMeasurementInterval(metadata.processCpu, intervalNs, true);
    setMeasurementInterval(metadata.processIo, intervalNs, true);

    std::unordered_map<ProcessIdentity, const ProcessRecord*, IdentityHash> old;
    old.reserve(previous->processes.size());
    for (const auto& record : previous->processes)
        old.emplace(record.identity, &record);

    for (auto& process : snapshot.processes) {
        if (process.identity.generationKind == GenerationKind::missing)
            continue;
        const auto match = old.find(process.identity);
        if (match == old.end())
            continue;
        const auto& before = match->second->counters;
        const auto& now = process.counters;
        if (now.kernel100ns < before.kernel100ns || now.user100ns < before.user100ns) {
            process.cpuPercent.state = SampleState::gap;
        } else {
            const auto ticks = static_cast<long double>(now.kernel100ns - before.kernel100ns)
                + static_cast<long double>(now.user100ns - before.user100ns);
            const auto percent = static_cast<double>(ticks / 10000000.L / seconds / snapshot.logicalProcessors * 100.L);
            process.cpuPercent = {std::clamp(percent, 0.0, 100.0), SampleState::valid, MetricSource::nativeProcess};
        }
        process.readIoBytesPerSecond = rate(now.readIoBytes, before.readIoBytes, seconds);
        process.writeIoBytesPerSecond = rate(now.writeIoBytes, before.writeIoBytes, seconds);
    }

    if (snapshot.processorState == SampleState::valid && previous->processorState == SampleState::valid
        && snapshot.systemKernel100ns >= previous->systemKernel100ns
        && snapshot.systemUser100ns >= previous->systemUser100ns
        && snapshot.systemIdle100ns >= previous->systemIdle100ns) {
        const auto elapsed = static_cast<long double>(snapshot.systemKernel100ns - previous->systemKernel100ns)
            + static_cast<long double>(snapshot.systemUser100ns - previous->systemUser100ns);
        const auto idle = static_cast<long double>(snapshot.systemIdle100ns - previous->systemIdle100ns);
        if (elapsed > 0 && idle <= elapsed)
            snapshot.systemCpuPercent = {static_cast<double>((elapsed - idle) / elapsed * 100.L), SampleState::valid, MetricSource::processorTime};
        else
            snapshot.systemCpuPercent.state = SampleState::gap;
    } else {
        snapshot.systemCpuPercent.state = snapshot.processorState == SampleState::valid
            && previous->processorState == SampleState::valid ? SampleState::gap : SampleState::unavailable;
    }
    bool invalidLogicalDelta = snapshot.processors.size() != previous->processors.size();
    for (std::size_t i = 0; i < snapshot.processors.size(); ++i) {
        auto& now = snapshot.processors[i];
        if (i >= previous->processors.size()) continue;
        const auto& before = previous->processors[i];
        if (now.group != before.group || now.index != before.index) {
            now.cpuPercent.state = SampleState::gap; invalidLogicalDelta = true; continue;
        }
        if (now.kernel100ns < before.kernel100ns || now.user100ns < before.user100ns || now.idle100ns < before.idle100ns) {
            now.cpuPercent.state = SampleState::gap; invalidLogicalDelta = true; continue;
        }
        const auto elapsed = static_cast<long double>(now.kernel100ns - before.kernel100ns) + now.user100ns - before.user100ns;
        const auto idle = static_cast<long double>(now.idle100ns - before.idle100ns);
        // A stationary core has no local ratio; it contributes no delta to the valid aggregate.
        if (idle > elapsed) invalidLogicalDelta = true;
        now.cpuPercent = elapsed > 0 && idle <= elapsed
            ? Metric<double>{static_cast<double>((elapsed - idle) / elapsed * 100.L), SampleState::valid, MetricSource::processorTime}
            : Metric<double>{0, SampleState::gap, MetricSource::processorTime};
    }
    if (snapshot.systemCpuPercent.valid() && invalidLogicalDelta)
        snapshot.systemCpuPercent.state = SampleState::gap;
    previous = snapshot;
}

}
