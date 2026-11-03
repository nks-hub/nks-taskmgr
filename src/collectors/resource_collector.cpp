#include <winsock2.h>
#include <ws2ipdef.h>
#include "resource_collector.hpp"
#include "gpu_telemetry.hpp"
#include "disk_telemetry.hpp"
#include "network_interface.hpp"
#include <setupapi.h>
#include <winioctl.h>
#include <ntddstor.h>
#include <ntdddisk.h>
#include <iphlpapi.h>
#include <ras.h>
#include <raserror.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <algorithm>
#include <memory>
#include <cwchar>
#include <iomanip>
#include <sstream>
#include <set>

namespace nks {
namespace {

Metric<double> find(const std::vector<NamedCounter>& values, const std::wstring& name)
{
    const auto match = std::find_if(values.begin(), values.end(), [&](const auto& item) { return item.name == name; });
    return match == values.end() ? Metric<double>{0, SampleState::unavailable, MetricSource::performanceCounter} : match->metric;
}

std::wstring luidName(LUID luid)
{
    wchar_t value[48]{};
    swprintf_s(value, L"luid_0x%08x_0x%08x", static_cast<unsigned>(luid.HighPart), luid.LowPart);
    return value;
}

std::wstring diskValue(const Metric<double>& metric, double multiplier, const wchar_t* unit)
{
    if (!metric.valid()) return L"Měření nedostupné";
    std::wostringstream text;
    text << std::fixed << std::setprecision(2) << metric.value * multiplier << unit;
    auto result = text.str();
    std::replace(result.begin(), result.end(), L'.', L',');
    return result;
}

}

void ResourceCollector::inventory()
{
    std::vector<ResourceRecord> next;
    std::map<std::wstring, std::uint32_t> nextNumbers;
    bool diskComplete = true;
    const auto set = SetupDiGetClassDevsW(&GUID_DEVINTERFACE_DISK, nullptr, nullptr, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
    if (set != INVALID_HANDLE_VALUE) {
        for (DWORD index = 0;; ++index) {
            SP_DEVICE_INTERFACE_DATA entry{sizeof(entry)};
            if (!SetupDiEnumDeviceInterfaces(set, nullptr, &GUID_DEVINTERFACE_DISK, index, &entry)) {
                diskComplete = diskComplete && GetLastError() == ERROR_NO_MORE_ITEMS;
                break;
            }
            DWORD size = 0;
            SetupDiGetDeviceInterfaceDetailW(set, &entry, nullptr, 0, &size, nullptr);
            if (size < sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W) || size > 64 * 1024) {
                diskComplete = false;
                continue;
            }
            std::vector<std::byte> data(size);
            auto* detail = reinterpret_cast<SP_DEVICE_INTERFACE_DETAIL_DATA_W*>(data.data());
            detail->cbSize = sizeof(*detail);
            SP_DEVINFO_DATA info{sizeof(info)};
            if (!SetupDiGetDeviceInterfaceDetailW(set, &entry, detail, size, nullptr, &info)) {
                diskComplete = false;
                continue;
            }
            wchar_t name[1024]{};
            if (!SetupDiGetDeviceRegistryPropertyW(set, &info, SPDRP_FRIENDLYNAME, nullptr,
                reinterpret_cast<BYTE*>(name), sizeof(name), nullptr))
                wcscpy_s(name, L"Diskové zařízení");
            ResourceRecord disk;
            disk.kind = ResourceKind::disk;
            disk.id = L"disk:" + std::wstring(detail->DevicePath);
            disk.title = L"Disk";
            disk.description = name;
            disk.measurements.primary = measurementMetadata(MeasurementUnit::percent, MeasurementNormalization::deviceActiveTime, MeasurementBasis::providerDefined, 0);
            disk.measurements.secondary = disk.measurements.tertiary = measurementMetadata(MeasurementUnit::bytesPerSecond,
                MeasurementNormalization::none, MeasurementBasis::providerDefined, 0);
            disk.measurements.capacity = measurementMetadata(MeasurementUnit::percent, MeasurementNormalization::deviceActiveTime, MeasurementBasis::gauge, 0);
            disk.facts = diskHardware(detail->DevicePath);
            const auto handle = CreateFileW(detail->DevicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                nullptr, OPEN_EXISTING, 0, nullptr);
            if (handle != INVALID_HANDLE_VALUE) {
                STORAGE_DEVICE_NUMBER number{};
                DWORD used = 0;
                if (DeviceIoControl(handle, IOCTL_STORAGE_GET_DEVICE_NUMBER, nullptr, 0, &number, sizeof(number), &used, nullptr)
                    && used >= sizeof(number)) {
                    nextNumbers.emplace(disk.id, number.DeviceNumber);
                    disk.title = L"Disk " + std::to_wstring(number.DeviceNumber);
                }
                CloseHandle(handle);
            }
            next.push_back(std::move(disk));
        }
        SetupDiDestroyDeviceInfoList(set);
    } else diskComplete = false;
    if (!diskComplete) {
        next.clear();
        for (const auto& item : devices)
            if (item.kind == ResourceKind::disk) next.push_back(item);
        nextNumbers = diskNumbers;
    }
    std::map<std::wstring, std::wstring> nextLuids;
    std::map<std::wstring, LUID> nextLuidValues;
    Microsoft::WRL::ComPtr<IDXGIFactory1> factory;
    bool gpuComplete = true;
    if (SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) {
        Microsoft::WRL::ComPtr<IDXGIAdapter1> adapter;
        for (UINT index = 0;; ++index) {
            const auto status = factory->EnumAdapters1(index, &adapter);
            if (status != S_OK) { gpuComplete = gpuComplete && status == DXGI_ERROR_NOT_FOUND; break; }
            DXGI_ADAPTER_DESC1 description{};
            const auto described = adapter->GetDesc1(&description);
            if (FAILED(described)) gpuComplete = false;
            if (SUCCEEDED(described) && !(description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE)
                && !gpuDisplayAlias(description.AdapterLuid)) {
                ResourceRecord gpu;
                gpu.kind = ResourceKind::gpu;
                gpu.id = luidName(description.AdapterLuid);
                gpu.title = L"GPU " + std::to_wstring(index);
                gpu.description = description.Description;
                gpu.capacity = static_cast<double>(description.DedicatedVideoMemory);
                gpu.sharedCapacity = static_cast<double>(description.SharedSystemMemory);
                const auto observedAt = measurementTime(std::chrono::steady_clock::now());
                const auto old = std::find_if(devices.begin(), devices.end(), [&](const auto& item) { return item.id == gpu.id; });
                gpu.measurements.capacity = gpu.measurements.sharedCapacity = measurementMetadata(MeasurementUnit::bytes,
                    MeasurementNormalization::none, MeasurementBasis::gauge, observedAt, old == devices.end() ? 0 : old->measurements.capacity.observedAtNs);
                gpu.measurements.primary = measurementMetadata(MeasurementUnit::percent, MeasurementNormalization::busiestGpuEngine, MeasurementBasis::providerDefined, 0);
                gpu.measurements.engineUtilization = measurementMetadata(MeasurementUnit::percent, MeasurementNormalization::none, MeasurementBasis::providerDefined, 0);
                gpu.measurements.secondary = gpu.measurements.tertiary = measurementMetadata(MeasurementUnit::bytes, MeasurementNormalization::none, MeasurementBasis::gauge, 0);
                gpu.measurements.temperature = measurementMetadata(MeasurementUnit::celsius, MeasurementNormalization::none, MeasurementBasis::gauge, 0);
                if (old != devices.end()) {
                    gpu.temperature = old->temperature;
                    gpu.measurements.temperature = old->measurements.temperature;
                }
                nextLuids.emplace(gpu.id, gpu.id);
                nextLuidValues.emplace(gpu.id, description.AdapterLuid);
                next.push_back(std::move(gpu));
            }
            adapter.Reset();
        }
    } else gpuComplete = false;
    if (!gpuComplete) {
        std::erase_if(next, [](const auto& item) { return item.kind == ResourceKind::gpu; });
        for (const auto& item : devices)
            if (item.kind == ResourceKind::gpu) next.push_back(item);
        nextLuids = gpuLuids;
        nextLuidValues = gpuLuidValues;
    }
    std::sort(next.begin(), next.end(), [&](const auto& a, const auto& b) {
        if (a.kind != b.kind) return a.kind < b.kind;
        if (a.kind == ResourceKind::disk) {
            const auto an = nextNumbers.contains(a.id) ? nextNumbers.at(a.id) : MAXDWORD;
            const auto bn = nextNumbers.contains(b.id) ? nextNumbers.at(b.id) : MAXDWORD;
            if (an != bn) return an < bn;
        }
        return a.id < b.id;
    });
    devices = std::move(next);
    diskNumbers = std::move(nextNumbers);
    std::erase_if(diskHealthFacts, [&](const auto& item) { return !diskNumbers.contains(item.first); });
    gpuLuids = std::move(nextLuids);
    gpuLuidValues = std::move(nextLuidValues);
    disksFresh = diskComplete;
    gpusFresh = gpuComplete;
    inventoryAt = std::chrono::steady_clock::now();
    telemetryAt = {};
}

void ResourceCollector::networks(ResourceSnapshot& snapshot)
{
    MIB_IF_TABLE2* raw = nullptr;
    if (GetIfTable2(&raw) != NO_ERROR) {
        for (auto item : lastNetworks) {
            item.primary.state = item.secondary.state = SampleState::unavailable;
            setMeasurementInterval(item.measurements.primary, 0, false);
            setMeasurementInterval(item.measurements.secondary, 0, false);
            item.status = L"Měření nedostupné";
            snapshot.devices.push_back(std::move(item));
        }
        previousNetworks.clear();
        return;
    }
    const auto observed = std::chrono::steady_clock::now();
    const auto observedNs = measurementTime(observed);
    std::unique_ptr<MIB_IF_TABLE2, decltype(&FreeMibTable)> table(raw, FreeMibTable);
    std::map<std::uint64_t, NetworkBefore> next;
    std::set<std::uint64_t> vpnConnections;
    DWORD bytes = sizeof(RASCONNW), count{};
    std::vector<RASCONNW> connections(1); connections[0].dwSize = sizeof(RASCONNW);
    auto rasResult = RasEnumConnectionsW(connections.data(), &bytes, &count);
    if (rasResult == ERROR_BUFFER_TOO_SMALL && bytes >= sizeof(RASCONNW) && bytes <= 1024 * sizeof(RASCONNW)) {
        connections.resize((bytes + sizeof(RASCONNW) - 1) / sizeof(RASCONNW));
        for (auto& connection : connections) connection.dwSize = sizeof(RASCONNW);
        rasResult = RasEnumConnectionsW(connections.data(), &bytes, &count);
    }
    if (rasResult == ERROR_SUCCESS && count <= connections.size()) {
        for (DWORD i = 0; i < count; ++i) {
            const auto& connection = connections[i];
            RASCONNSTATUSW status{}; status.dwSize = sizeof(status);
            if (_wcsicmp(connection.szDeviceType, RASDT_Vpn) != 0
                || RasGetConnectStatusW(connection.hrasconn, &status) != ERROR_SUCCESS || status.rasconnstate != RASCS_Connected) continue;
            // RASCONN::luid identifies the logon session, not the network interface.
            NET_LUID luid{};
            if (ConvertInterfaceAliasToLuid(connection.szEntryName, &luid) == NO_ERROR) vpnConnections.insert(luid.Value);
        }
    }
    nextNetworks.clear();
    nextNetworks.reserve(raw->NumEntries);
    try {
        for (ULONG i = 0; i < raw->NumEntries; ++i) {
            const auto& row = raw->Table[i];
            if (row.InterfaceAndOperStatusFlags.FilterInterface) continue;
            const bool connected = row.OperStatus == IfOperStatusUp;
            const bool rasVpn = vpnConnections.contains(row.InterfaceLuid.Value);
            const NetworkPresentationKey presentation{row.InterfaceLuid.Value, row.ReceiveLinkSpeed, row.TransmitLinkSpeed,
                row.Mtu, row.Type, static_cast<std::uint32_t>(row.TunnelType), static_cast<std::uint32_t>(row.OperStatus),
                row.InterfaceAndOperStatusFlags.HardwareInterface != 0, rasVpn};
            const auto before = previousNetworks.find(row.InterfaceLuid.Value);
            ResourceRecord network;
            const bool reused = before != previousNetworks.end() && reuseNetworkPresentation(network,
                lastNetworks.at(before->second.recordIndex), before->second.presentation, presentation,
                before->second.titlePrefixCharacters, row.Alias, row.Description);
            if (!reused) {
                const auto classification = classifyNetworkInterface({row.Type, row.TunnelType,
                    row.InterfaceAndOperStatusFlags.HardwareInterface != 0, connected, rasVpn, row.Alias, row.Description});
                network.id = L"network:" + std::to_wstring(row.InterfaceLuid.Value);
                network.kind = ResourceKind::network;
                network.title = row.Alias;
                if (classification == NetworkInterfaceClass::vpn) network.title = L"VPN · " + network.title;
                else if (classification == NetworkInterfaceClass::auxiliary) network.title = L"Virtuální · " + network.title;
                network.description = row.Description;
                network.capacity = static_cast<double>(std::max(row.TransmitLinkSpeed, row.ReceiveLinkSpeed));
                network.status = connected ? L"Připojeno" : L"Odpojeno";
                network.facts = {{L"Rychlost linky ↓", hardwareRate(row.ReceiveLinkSpeed)}, {L"Rychlost linky ↑", hardwareRate(row.TransmitLinkSpeed)},
                    {L"MTU", std::to_wstring(row.Mtu) + L" B"}, {L"Připojení", row.Type == IF_TYPE_IEEE80211 ? L"Wi-Fi" : row.Type == IF_TYPE_ETHERNET_CSMACD ? L"Ethernet"
                        : row.Type == IF_TYPE_SOFTWARE_LOOPBACK ? L"Loopback" : row.Type == IF_TYPE_TUNNEL ? L"Tunel" : row.Type == IF_TYPE_PPP ? L"PPP" : L"Jiné"},
                    {L"Druh rozhraní", networkInterfaceClassName(classification)}, {L"LUID rozhraní", std::to_wstring(row.InterfaceLuid.Value)},
                    {L"Stav rozhraní Windows", std::to_wstring(static_cast<unsigned>(row.OperStatus))},
                    {L"Typ rozhraní Windows", std::to_wstring(row.Type)}, {L"Typ tunelu Windows", std::to_wstring(static_cast<unsigned>(row.TunnelType))}};
            }
            network.primary = {0, connected ? SampleState::first : SampleState::unavailable, MetricSource::networkInterface};
            network.secondary = network.primary;
            const auto beforeNs = before == previousNetworks.end() ? 0 : measurementTime(before->second.measuredAt);
            network.measurements.primary = network.measurements.secondary = measurementMetadata(MeasurementUnit::bitsPerSecond,
                MeasurementNormalization::none, MeasurementBasis::elapsedDelta, observedNs, beforeNs);
            network.measurements.capacity = measurementMetadata(MeasurementUnit::bitsPerSecond, MeasurementNormalization::none,
                MeasurementBasis::gauge, observedNs, beforeNs);
            if (connected && before != previousNetworks.end() && before->second.connected) {
                const auto intervalNs = std::chrono::duration_cast<std::chrono::nanoseconds>(observed - before->second.measuredAt).count();
                const auto seconds = static_cast<double>(intervalNs) / 1'000'000'000.;
                network.primary = networkRate(row.InOctets, before->second.received, seconds);
                network.secondary = networkRate(row.OutOctets, before->second.sent, seconds);
                setMeasurementInterval(network.measurements.primary, intervalNs, network.primary.valid());
                setMeasurementInterval(network.measurements.secondary, intervalNs, network.secondary.valid());
            }
            next.emplace(row.InterfaceLuid.Value, NetworkBefore{row.InOctets, row.OutOctets, connected, observed,
                nextNetworks.size(), network.title.size() - std::wcslen(row.Alias), presentation});
            snapshot.devices.push_back(network);
            nextNetworks.push_back(std::move(network));
        }
    } catch (...) {
        // Reuse moves owned records; a partial loop cannot remain a fallback cache.
        lastNetworks.clear(); nextNetworks.clear(); previousNetworks.clear();
        throw;
    }
    lastNetworks.swap(nextNetworks);
    nextNetworks.clear();
    previousNetworks = std::move(next);
}

ResourceSnapshot ResourceCollector::collect(bool cpuSensors)
{
    const auto now = std::chrono::steady_clock::now();
    if (now - inventoryAt > std::chrono::seconds(15))
        inventory();
    const auto collected = counters.collect();
    ResourceSnapshot result;
    result.cpuSensorFacts = cpuTemperature.collect(cpuSensors);
    if (now - upsAt >= std::chrono::seconds(5)) { upsFacts = upsHardware(); upsAt = now; }
    result.upsFacts = upsFacts;
    result.measuredAt = std::chrono::steady_clock::now();
    result.diskCountersAvailable = counters.available(0);
    result.gpuCountersAvailable = counters.available(3);
    result.devices = devices;
    const auto idle = collected ? counters.read(0) : std::vector<NamedCounter>{};
    const auto reads = collected ? counters.read(1) : std::vector<NamedCounter>{};
    const auto writes = collected ? counters.read(2) : std::vector<NamedCounter>{};
    const auto engines = collected ? counters.read(3) : std::vector<NamedCounter>{};
    const auto dedicated = collected ? counters.read(4) : std::vector<NamedCounter>{};
    const auto shared = collected ? counters.read(5) : std::vector<NamedCounter>{};
    std::array<std::vector<NamedCounter>, 6> diskDetails;
    for (std::size_t i = 0; i < diskDetails.size(); ++i)
        if (collected) diskDetails[i] = counters.read(i + 6);
    const bool diskHealthDue = now - diskHealthAt >= std::chrono::seconds(30);
    const bool telemetryDue = now - telemetryAt >= std::chrono::seconds(5);
    for (auto& item : result.devices) {
        if ((item.kind == ResourceKind::disk && !disksFresh) || (item.kind == ResourceKind::gpu && !gpusFresh)) {
            item.primary.state = item.secondary.state = item.tertiary.state = SampleState::unavailable;
            item.status = L"Inventář není aktuální";
            item.temperature.state = SampleState::unavailable;
            continue;
        }
        if (item.kind == ResourceKind::gpu) {
            item.measurements.primary = item.measurements.engineUtilization = counters.measurement(3);
            item.measurements.primary.normalization = MeasurementNormalization::busiestGpuEngine;
            item.measurements.secondary = counters.measurement(4);
            item.measurements.tertiary = counters.measurement(5);
            item.engines = gpuEngines(engines, gpuLuids.at(item.id));
            item.primary = busiestGpuEngine(item.engines);
            item.secondary = gpuMemoryUsage(dedicated, item.id);
            item.tertiary = gpuMemoryUsage(shared, item.id);
            if (telemetryDue) {
                item.temperature = gpuTemperature(gpuLuidValues.at(item.id));
                item.measurements.temperature = measurementMetadata(MeasurementUnit::celsius, MeasurementNormalization::none,
                    MeasurementBasis::gauge, measurementTime(std::chrono::steady_clock::now()), item.measurements.temperature.observedAtNs);
                const auto cached = std::find_if(devices.begin(), devices.end(), [&](const auto& device) { return device.id == item.id; });
                cached->temperature = item.temperature;
                cached->measurements.temperature = item.measurements.temperature;
            }
            continue;
        }
        const auto number = diskNumbers.find(item.id);
        if (number == diskNumbers.end()) continue;
        if (diskHealthDue || !diskHealthFacts.contains(item.id))
            diskHealthFacts[item.id] = diskHealth(item.id.substr(5));
        const auto& healthFacts = diskHealthFacts.at(item.id);
        item.facts.insert(item.facts.end(), healthFacts.begin(), healthFacts.end());
        const auto prefix = std::to_wstring(number->second);
        std::wstring instance;
        const std::array<const std::vector<NamedCounter>*, 4> instances{&idle, &reads, &writes, &diskDetails[3]};
        for (const auto* values : instances) {
            const auto match = std::find_if(values->begin(), values->end(), [&](const auto& v) {
                return v.name == prefix || v.name.starts_with(prefix + L" ");
            });
            if (match != values->end()) { instance = match->name; break; }
        }
        if (!instance.empty()) item.title = L"Disk " + instance;
        item.primary = find(idle, instance);
        item.measurements.primary = counters.measurement(0);
        if (item.primary.valid()) item.primary.value = std::clamp(100 - item.primary.value, 0.0, 100.0);
        item.secondary = find(reads, instance);
        item.tertiary = find(writes, instance);
        item.measurements.secondary = counters.measurement(1);
        item.measurements.tertiary = counters.measurement(2);
        constexpr std::array labels{L"Průměrná odezva", L"Odezva čtení", L"Odezva zápisu", L"Fronta požadavků", L"Čtení IOPS", L"Zápis IOPS"};
        for (std::size_t i = 0; i < diskDetails.size(); ++i)
            item.facts.push_back({labels[i], diskValue(find(diskDetails[i], instance), i < 3 ? 1000 : 1, i < 3 ? L" ms" : i > 3 ? L" operací/s" : L" požadavků")});
        item.capacity = 100;
    }
    networks(result);
    if (telemetryDue) telemetryAt = std::chrono::steady_clock::now();
    if (diskHealthDue) diskHealthAt = std::chrono::steady_clock::now();
    return result;
}

}
