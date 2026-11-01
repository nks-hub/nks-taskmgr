#include "pdh_counters.hpp"
#include <pdhmsg.h>
#include <cmath>
#include <limits>

namespace nks {

PdhCounters::PdhCounters() { retryMissing(); }

void PdhCounters::retryMissing()
{
    retryAt = std::chrono::steady_clock::now() + std::chrono::seconds(15);
    if (!query && PdhOpenQueryW(nullptr, 0, &query) != ERROR_SUCCESS)
        return;
    const std::array paths{
        L"\\PhysicalDisk(*)\\% Idle Time",
        L"\\PhysicalDisk(*)\\Disk Read Bytes/sec",
        L"\\PhysicalDisk(*)\\Disk Write Bytes/sec",
        L"\\GPU Engine(*)\\Utilization Percentage",
        L"\\GPU Adapter Memory(*)\\Dedicated Usage",
        L"\\GPU Adapter Memory(*)\\Shared Usage",
        L"\\PhysicalDisk(*)\\Avg. Disk sec/Transfer",
        L"\\PhysicalDisk(*)\\Avg. Disk sec/Read",
        L"\\PhysicalDisk(*)\\Avg. Disk sec/Write",
        L"\\PhysicalDisk(*)\\Current Disk Queue Length",
        L"\\PhysicalDisk(*)\\Disk Reads/sec",
        L"\\PhysicalDisk(*)\\Disk Writes/sec"
    };
    for (std::size_t i = 0; i < paths.size(); ++i) {
        if (!counters[i] && PdhAddEnglishCounterW(query, paths[i], 0, &counters[i]) != ERROR_SUCCESS)
            counters[i] = nullptr;
    }
}

PdhCounters::~PdhCounters() { if (query) PdhCloseQuery(query); }

bool PdhCounters::collect()
{
    if (std::chrono::steady_clock::now() >= retryAt) retryMissing();
    if (!query) return false;
    const auto status = PdhCollectQueryData(query);
    previousObservedAtNs = observedAtNs;
    observedAtNs = measurementTime(std::chrono::steady_clock::now());
    return status == ERROR_SUCCESS;
}

MeasurementMetadata PdhCounters::measurement(std::size_t index) const
{
    constexpr std::array units{MeasurementUnit::percent, MeasurementUnit::bytesPerSecond, MeasurementUnit::bytesPerSecond,
        MeasurementUnit::percent, MeasurementUnit::bytes, MeasurementUnit::bytes, MeasurementUnit::seconds, MeasurementUnit::seconds,
        MeasurementUnit::seconds, MeasurementUnit::count, MeasurementUnit::operationsPerSecond, MeasurementUnit::operationsPerSecond};
    const auto unit = units.at(index);
    const auto basis = index == 4 || index == 5 || index == 9 ? MeasurementBasis::gauge : MeasurementBasis::providerDefined;
    const auto normalization = index == 0 ? MeasurementNormalization::deviceActiveTime : MeasurementNormalization::none;
    // Cooked PDH values do not expose their calculation window in this API.
    return measurementMetadata(unit, normalization, basis, observedAtNs, previousObservedAtNs);
}

std::vector<NamedCounter> PdhCounters::read(std::size_t index) const
{
    if (!counters.at(index))
        return {};
    const auto flags = PDH_FMT_DOUBLE | PDH_FMT_NOCAP100;
    constexpr DWORD maximumBytes = 16 * 1024 * 1024;
    for (unsigned attempt = 0; attempt < 3; ++attempt) {
        DWORD bytes = 0, count = 0;
        if (PdhGetFormattedCounterArrayW(counters[index], flags, &bytes, &count, nullptr) != PDH_MORE_DATA
            || bytes == 0 || bytes > maximumBytes)
            return {};
        std::vector<std::byte> storage(bytes);
        auto* items = reinterpret_cast<PDH_FMT_COUNTERVALUE_ITEM_W*>(storage.data());
        const auto status = PdhGetFormattedCounterArrayW(counters[index], flags, &bytes, &count, items);
        if (status == PDH_MORE_DATA)
            continue;
        if (status != ERROR_SUCCESS || count > storage.size() / sizeof(*items))
            return {};
        std::vector<NamedCounter> result;
        result.reserve(count);
        for (DWORD i = 0; i < count; ++i) {
            const auto& value = items[i].FmtValue;
            const bool valid = (value.CStatus == PDH_CSTATUS_VALID_DATA || value.CStatus == PDH_CSTATUS_NEW_DATA)
                && std::isfinite(value.doubleValue) && value.doubleValue >= 0;
            result.push_back({items[i].szName, {valid ? value.doubleValue : 0,
                valid ? SampleState::valid : SampleState::unavailable, MetricSource::performanceCounter}});
        }
        return result;
    }
    return {};
}

}
