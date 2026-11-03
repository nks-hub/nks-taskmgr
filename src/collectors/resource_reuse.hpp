#pragma once

#include "core/resource.hpp"
#include <algorithm>
#include <string_view>
#include <utility>

namespace nks {

struct NetworkPresentationKey {
    std::uint64_t luid{}, receiveLinkSpeed{}, transmitLinkSpeed{};
    std::uint32_t mtu{}, type{}, tunnelType{}, operStatus{};
    bool hardware{}, rasVpn{};
    bool operator==(const NetworkPresentationKey&) const = default;
};

inline bool reuseNetworkPresentation(ResourceRecord& target, ResourceRecord& cached,
    const NetworkPresentationKey& previous, const NetworkPresentationKey& current,
    std::size_t titlePrefixCharacters, std::wstring_view alias, std::wstring_view description)
{
    if (previous != current || cached.title.size() < titlePrefixCharacters
        || std::wstring_view(cached.title).substr(titlePrefixCharacters) != alias || cached.description != description)
        return false;
    target = std::move(cached);
    return true;
}

inline Metric<double> busiestGpuEngine(std::span<const GpuEngine> engines)
{
    double busiest = 0;
    for (const auto& engine : engines) {
        if (!engine.utilization.valid()) return {0, SampleState::unavailable, MetricSource::performanceCounter};
        busiest = std::max(busiest, engine.utilization.value);
    }
    return {busiest, engines.empty() ? SampleState::unavailable : SampleState::valid, MetricSource::performanceCounter};
}

}
