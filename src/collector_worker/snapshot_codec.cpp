#include "snapshot_codec.hpp"

namespace nks::collector {
namespace {

template<class T> void enumeration(Writer& out, T value) { out.number(static_cast<std::uint32_t>(value)); }
void metric(Writer& out, const Metric<double>& value) {
    out.real(value.value); enumeration(out, value.state); enumeration(out, value.source);
}
Metric<double> metric(Reader& in) {
    return {in.real(), in.enumeration<SampleState>(7), in.enumeration<MetricSource>(6)};
}
void metadata(Writer& out, const MeasurementMetadata& value) {
    out.number(static_cast<std::uint8_t>(value.unit)); out.number(static_cast<std::uint8_t>(value.normalization));
    out.number(static_cast<std::uint8_t>(value.basis)); out.number(static_cast<std::uint8_t>(value.intervalState));
    out.number(value.normalizationDivisor); out.signedNumber(value.observedAtNs);
    out.signedNumber(value.intervalNs); out.signedNumber(value.cadenceNs);
}
MeasurementMetadata metadata(Reader& in) {
    MeasurementMetadata value;
    const auto unit = in.number<std::uint8_t>(), normalization = in.number<std::uint8_t>();
    const auto basis = in.number<std::uint8_t>(), state = in.number<std::uint8_t>();
    if (unit > 8 || normalization > 4 || basis > 4 || state > 3)
        throw std::runtime_error("Invalid collector measurement metadata");
    value.unit = static_cast<MeasurementUnit>(unit); value.normalization = static_cast<MeasurementNormalization>(normalization);
    value.basis = static_cast<MeasurementBasis>(basis); value.intervalState = static_cast<MeasurementIntervalState>(state);
    value.normalizationDivisor = in.number<std::uint32_t>(); value.observedAtNs = in.signedNumber();
    value.intervalNs = in.signedNumber(); value.cadenceNs = in.signedNumber();
    if (value.observedAtNs < 0 || value.intervalNs < 0 || value.cadenceNs < 0)
        throw std::runtime_error("Invalid collector measurement time");
    return value;
}
void facts(Writer& out, const std::vector<HardwareFact>& values) {
    out.count(values.size(), 4096);
    for (const auto& value : values) { out.text(value.name); out.text(value.value); }
}
std::vector<HardwareFact> facts(Reader& in) {
    const auto count = in.count(4096, 8); std::vector<HardwareFact> values; values.reserve(count);
    for (std::uint32_t i = 0; i < count; ++i) values.push_back({in.text(), in.text()});
    return values;
}
void identity(Writer& out, const ProcessIdentity& value) {
    out.number(value.boot.low); out.number(value.boot.high); out.number(value.pid);
    enumeration(out, value.generationKind); out.number(value.generation);
}
ProcessIdentity identity(Reader& in) {
    ProcessIdentity value;
    value.boot = {in.number<std::uint64_t>(), in.number<std::uint64_t>()};
    value.pid = in.number<std::uint32_t>(); value.generationKind = in.enumeration<GenerationKind>(2);
    value.generation = in.number<std::uint64_t>(); return value;
}
void counters(Writer& out, const ProcessCounters& value) {
    out.number(value.kernel100ns); out.number(value.user100ns); out.number(value.readIoBytes); out.number(value.writeIoBytes);
    out.number(value.workingSetBytes); out.number(value.privateBytes); out.number(value.threads); out.number(value.handles);
}
ProcessCounters counters(Reader& in) {
    ProcessCounters value;
    value.kernel100ns = in.number<std::uint64_t>(); value.user100ns = in.number<std::uint64_t>();
    value.readIoBytes = in.number<std::uint64_t>(); value.writeIoBytes = in.number<std::uint64_t>();
    value.workingSetBytes = in.number<std::uint64_t>(); value.privateBytes = in.number<std::uint64_t>();
    value.threads = in.number<std::uint32_t>(); value.handles = in.number<std::uint32_t>(); return value;
}
void memory(Writer& out, const SystemMemory& value) {
    out.number(value.physicalBytes); out.number(value.availableBytes); out.number(value.committedBytes); out.number(value.commitLimitBytes);
    out.number(value.systemCacheBytes); out.number(value.pagedPoolBytes); out.number(value.nonpagedPoolBytes); enumeration(out, value.state);
    out.number(value.pageFileBytes); out.number(value.pageFileUsedBytes); out.number(value.pageFilePeakBytes);
    enumeration(out, value.pageFileState); out.number(value.installedBytes);
}
SystemMemory memory(Reader& in) {
    SystemMemory value;
    value.physicalBytes = in.number<std::uint64_t>(); value.availableBytes = in.number<std::uint64_t>();
    value.committedBytes = in.number<std::uint64_t>(); value.commitLimitBytes = in.number<std::uint64_t>();
    value.systemCacheBytes = in.number<std::uint64_t>(); value.pagedPoolBytes = in.number<std::uint64_t>();
    value.nonpagedPoolBytes = in.number<std::uint64_t>(); value.state = in.enumeration<SampleState>(7);
    value.pageFileBytes = in.number<std::uint64_t>(); value.pageFileUsedBytes = in.number<std::uint64_t>();
    value.pageFilePeakBytes = in.number<std::uint64_t>(); value.pageFileState = in.enumeration<SampleState>(7);
    value.installedBytes = in.number<std::uint64_t>(); return value;
}
template<class Clock> std::int64_t ticks(typename Clock::time_point value) {
    return std::chrono::duration_cast<std::chrono::nanoseconds>(value.time_since_epoch()).count();
}
template<class Clock> typename Clock::time_point timePoint(Reader& in) {
    const auto value = in.signedNumber();
    if (value < 0) throw std::runtime_error("Invalid collector timestamp");
    return typename Clock::time_point(std::chrono::duration_cast<typename Clock::duration>(std::chrono::nanoseconds(value)));
}
std::chrono::nanoseconds duration(Reader& in) {
    const auto value = in.signedNumber();
    if (value < 0) throw std::runtime_error("Invalid collector duration");
    return std::chrono::nanoseconds(value);
}
void processMetadata(Writer& out, const ProcessMeasurements& value) {
    metadata(out, value.processCpu); metadata(out, value.processIo); metadata(out, value.processMemory);
    metadata(out, value.systemCpu); metadata(out, value.logicalCpu); metadata(out, value.systemMemory);
}
ProcessMeasurements processMetadata(Reader& in) {
    return {metadata(in), metadata(in), metadata(in), metadata(in), metadata(in), metadata(in)};
}
void resourceMetadata(Writer& out, const ResourceMeasurements& value) {
    metadata(out, value.primary); metadata(out, value.secondary); metadata(out, value.tertiary); metadata(out, value.temperature);
    metadata(out, value.capacity); metadata(out, value.sharedCapacity); metadata(out, value.engineUtilization);
}
ResourceMeasurements resourceMetadata(Reader& in) {
    return {metadata(in), metadata(in), metadata(in), metadata(in), metadata(in), metadata(in), metadata(in)};
}

}

std::vector<std::byte> encode(const ProcessSnapshot& value)
{
    Writer out;
    out.number(value.boot.id.low); out.number(value.boot.id.high); enumeration(out, value.boot.state); out.number(value.bootTime);
    facts(out, value.memoryFacts); facts(out, value.cpuFacts); facts(out, value.boardFacts);
    out.signedNumber(ticks<std::chrono::steady_clock>(value.measuredAt)); out.signedNumber(ticks<std::chrono::system_clock>(value.wallTime));
    out.signedNumber(value.collectionTime.count()); out.signedNumber(value.queryTime.count());
    out.signedNumber(value.parseTime.count()); out.signedNumber(value.systemTime.count());
    out.number(value.logicalProcessors); out.text(value.cpuModel); out.count(value.processors.size(), 4096);
    for (const auto& cpu : value.processors) {
        out.number(cpu.group); out.number(cpu.index); out.number(cpu.kernel100ns); out.number(cpu.user100ns); out.number(cpu.idle100ns);
        metric(out, cpu.cpuPercent);
    }
    out.number(value.systemKernel100ns); out.number(value.systemUser100ns); out.number(value.systemIdle100ns);
    enumeration(out, value.processorState); metric(out, value.systemCpuPercent); memory(out, value.memory);
    out.count(value.processes.size(), 131072);
    for (const auto& process : value.processes) {
        identity(out, process.identity); out.number(process.parentPid); out.text(process.name); counters(out, process.counters);
        metric(out, process.cpuPercent); metric(out, process.readIoBytesPerSecond); metric(out, process.writeIoBytesPerSecond);
        out.number(process.created100ns);
    }
    processMetadata(out, value.measurements); return std::move(out.bytes);
}

ProcessSnapshot decodeProcesses(std::span<const std::byte> payload)
{
    Reader in(payload); ProcessSnapshot value;
    value.boot.id = {in.number<std::uint64_t>(), in.number<std::uint64_t>()};
    value.boot.state = in.enumeration<SampleState>(7); value.bootTime = in.number<std::uint64_t>();
    value.memoryFacts = facts(in); value.cpuFacts = facts(in); value.boardFacts = facts(in);
    value.measuredAt = timePoint<std::chrono::steady_clock>(in); value.wallTime = timePoint<std::chrono::system_clock>(in);
    value.collectionTime = duration(in); value.queryTime = duration(in); value.parseTime = duration(in); value.systemTime = duration(in);
    value.logicalProcessors = in.number<std::uint32_t>(); value.cpuModel = in.text();
    const auto cpuCount = in.count(4096, 44); value.processors.reserve(cpuCount);
    for (std::uint32_t i = 0; i < cpuCount; ++i) {
        LogicalProcessor cpu;
        cpu.group = in.number<std::uint16_t>(); cpu.index = in.number<std::uint16_t>();
        cpu.kernel100ns = in.number<std::uint64_t>(); cpu.user100ns = in.number<std::uint64_t>(); cpu.idle100ns = in.number<std::uint64_t>();
        cpu.cpuPercent = metric(in); value.processors.push_back(cpu);
    }
    value.systemKernel100ns = in.number<std::uint64_t>(); value.systemUser100ns = in.number<std::uint64_t>();
    value.systemIdle100ns = in.number<std::uint64_t>(); value.processorState = in.enumeration<SampleState>(7);
    value.systemCpuPercent = metric(in); value.memory = memory(in);
    const auto count = in.count(131072, 152); value.processes.reserve(count);
    for (std::uint32_t i = 0; i < count; ++i) {
        ProcessRecord process;
        process.identity = identity(in); process.parentPid = in.number<std::uint32_t>(); process.name = in.text(); process.counters = counters(in);
        process.cpuPercent = metric(in); process.readIoBytesPerSecond = metric(in); process.writeIoBytesPerSecond = metric(in);
        process.created100ns = in.number<std::uint64_t>(); value.processes.push_back(std::move(process));
    }
    value.measurements = processMetadata(in); in.finished(); return value;
}

std::vector<std::byte> encode(const ResourceSnapshot& value)
{
    Writer out;
    facts(out, value.upsFacts); out.signedNumber(ticks<std::chrono::steady_clock>(value.measuredAt));
    out.number<std::uint32_t>(value.diskCountersAvailable); out.number<std::uint32_t>(value.gpuCountersAvailable);
    out.count(value.devices.size(), 4096);
    for (const auto& device : value.devices) {
        facts(out, device.facts); out.text(device.id); enumeration(out, device.kind);
        out.text(device.title); out.text(device.description); out.text(device.status);
        metric(out, device.primary); metric(out, device.secondary); metric(out, device.tertiary);
        out.real(device.capacity); out.real(device.sharedCapacity); out.count(device.engines.size(), 256);
        for (const auto& engine : device.engines) { out.text(engine.id); out.text(engine.name); metric(out, engine.utilization); }
        metric(out, device.temperature); resourceMetadata(out, device.measurements);
    }
    return std::move(out.bytes);
}

ResourceSnapshot decodeResources(std::span<const std::byte> payload)
{
    Reader in(payload); ResourceSnapshot value;
    value.upsFacts = facts(in); value.measuredAt = timePoint<std::chrono::steady_clock>(in);
    value.diskCountersAvailable = in.boolean(); value.gpuCountersAvailable = in.boolean();
    const auto count = in.count(4096, 332); value.devices.reserve(count);
    for (std::uint32_t i = 0; i < count; ++i) {
        ResourceRecord device;
        device.facts = facts(in); device.id = in.text(); device.kind = in.enumeration<ResourceKind>(4);
        device.title = in.text(); device.description = in.text(); device.status = in.text();
        device.primary = metric(in); device.secondary = metric(in); device.tertiary = metric(in);
        device.capacity = in.real(); device.sharedCapacity = in.real(); const auto engines = in.count(256, 24);
        device.engines.reserve(engines);
        for (std::uint32_t j = 0; j < engines; ++j) device.engines.push_back({in.text(), in.text(), metric(in)});
        device.temperature = metric(in); device.measurements = resourceMetadata(in); value.devices.push_back(std::move(device));
    }
    in.finished(); return value;
}

}
