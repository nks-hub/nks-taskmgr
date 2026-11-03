#include "collectors/resource_reuse.hpp"
#include <iostream>
#include <limits>
#include <stdexcept>

namespace {

void require(bool value, const char* message)
{
    if (!value) throw std::runtime_error(message);
}

nks::ResourceRecord network(std::wstring_view prefix, std::wstring_view alias)
{
    nks::ResourceRecord result;
    result.kind = nks::ResourceKind::network;
    result.id = L"network:7";
    result.title = std::wstring(prefix) + std::wstring(alias);
    result.description = L"Adapter description";
    result.status = L"Connected";
    result.capacity = 1'000'000'000;
    result.facts = {{L"MTU", L"1500 B"}, {L"Link", L"1 Gbit/s"}};
    result.primary = {800, nks::SampleState::valid, nks::MetricSource::networkInterface};
    result.measurements.primary = nks::measurementMetadata(nks::MeasurementUnit::bitsPerSecond,
        nks::MeasurementNormalization::none, nks::MeasurementBasis::elapsedDelta, 2'000'000'000, 1'000'000'000);
    return result;
}

void unchangedNetworkPresentationMovesOwnedStorage()
{
    const nks::NetworkPresentationKey key{7, 1'000'000'000, 1'000'000'000, 1500, 6, 0, 1, true, false};
    for (const auto prefix : {L"", L"VPN · ", L"Virtuální · "}) {
        auto cached = network(prefix, L"VPN · Alias");
        const auto heldSnapshot = cached;
        const auto* facts = cached.facts.data();
        nks::ResourceRecord result;
        require(nks::reuseNetworkPresentation(result, cached, key, key, std::wstring_view(prefix).size(),
            L"VPN · Alias", L"Adapter description"), "Unchanged physical/VPN/auxiliary presentation was rebuilt");
        require(result.facts.data() == facts && result.facts.size() == 2, "Metadata reuse copied its owned fact storage");
        require(result.id == heldSnapshot.id && result.title == heldSnapshot.title && result.description == heldSnapshot.description
            && result.status == heldSnapshot.status && result.capacity == heldSnapshot.capacity, "Reused interface presentation changed");
        require(result.measurements.primary.observedAtNs == heldSnapshot.measurements.primary.observedAtNs
            && result.primary.value == heldSnapshot.primary.value, "Presentation move changed counter/timestamp storage");
        result.facts[0].value = L"9000 B";
        require(heldSnapshot.facts[0].value == L"1500 B", "A held network snapshot shares mutable cache facts");
    }
}

void changedNetworkPresentationCannotReuseOldFacts()
{
    const nks::NetworkPresentationKey key{7, 1'000'000'000, 1'000'000'000, 1500, 6, 0, 1, true, false};
    std::vector<nks::NetworkPresentationKey> changes;
    auto changed = key; changed.luid = 8; changes.push_back(changed);
    changed = key; changed.receiveLinkSpeed = 100'000'000; changes.push_back(changed);
    changed = key; changed.transmitLinkSpeed = 100'000'000; changes.push_back(changed);
    changed = key; changed.mtu = 9000; changes.push_back(changed);
    changed = key; changed.type = 23; changes.push_back(changed);
    changed = key; changed.tunnelType = 13; changes.push_back(changed);
    changed = key; changed.operStatus = 2; changes.push_back(changed);
    changed = key; changed.hardware = false; changes.push_back(changed);
    changed = key; changed.rasVpn = true; changes.push_back(changed);
    for (const auto& next : changes) {
        auto cached = network(L"VPN · ", L"Alias");
        const auto* facts = cached.facts.data();
        nks::ResourceRecord result;
        require(!nks::reuseNetworkPresentation(result, cached, key, next, 6, L"Alias", L"Adapter description"),
            "Changed fresh interface metadata retained stale presentation");
        require(cached.facts.data() == facts && cached.facts.size() == 2 && result.id.empty(),
            "Rejected cache reuse consumed the previous interface record");
    }
    for (const auto& names : {std::pair{L"Renamed", L"Adapter description"}, std::pair{L"Alias", L"Different adapter"}}) {
        auto cached = network(L"VPN · ", L"Alias");
        nks::ResourceRecord result;
        require(!nks::reuseNetworkPresentation(result, cached, key, key, 6, names.first, names.second),
            "Renamed interface or changed driver description retained stale VPN classification");
    }
}

nks::NamedCounter counter(const wchar_t* name, double value, nks::SampleState state = nks::SampleState::valid)
{
    return {name, {value, state, nks::MetricSource::performanceCounter}};
}

void gpuSummaryReusesTheSameAggregatedEngines()
{
    const std::wstring luid = L"luid_0x00000002_0xabcdef01";
    std::vector values{
        counter(L"pid_1_luid_0x00000002_0xABCDEF01_phys_0_eng_0_engtype_3D", 40),
        counter(L"pid_2_luid_0x00000002_0xABCDEF01_phys_0_eng_0_engtype_3D", 70),
        counter(L"pid_2_luid_0x00000002_0xABCDEF01_phys_0_eng_1_engtype_Copy", 20),
        counter(L"pid_1_luid_0x00000002_0xABCDEF02_phys_0_eng_0_engtype_3D", 99)
    };
    const auto compare = [&](const std::vector<nks::NamedCounter>& input) {
        const auto grouped = nks::gpuEngines(input, luid);
        const auto expected = nks::busiestGpuEngine(input, luid);
        const auto actual = nks::busiestGpuEngine(grouped);
        require(actual.value == expected.value && actual.state == expected.state && actual.source == expected.source,
            "Summary from retained engines differs from the original PDH parsing path");
        return actual;
    };
    require(compare(values).value == 100, "Process engine sums were not clamped before taking the maximum");
    values[0].metric.state = nks::SampleState::unavailable;
    require(!compare(values).valid(), "Unavailable contributing engine became a valid summary");
    values[0].metric.state = nks::SampleState::valid;
    values[0].metric.value = std::numeric_limits<double>::infinity();
    require(!compare(values).valid(), "Nonfinite contributing engine became a valid summary");
    values.erase(values.begin(), values.begin() + 2);
    require(compare(values).value == 20, "Disappearing engines retained an old peak");
    require(!compare({}).valid(), "Missing GPU topology became measured zero");
}

}

int main()
{
    try {
        unchangedNetworkPresentationMovesOwnedStorage();
        changedNetworkPresentationCannotReuseOldFacts();
        gpuSummaryReusesTheSameAggregatedEngines();
        std::cout << "PASS resource presentation reuse and single GPU aggregation\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "FAIL " << error.what() << '\n';
        return 1;
    }
}
