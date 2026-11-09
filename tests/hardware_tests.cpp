#include "core/hardware.hpp"
#include "collectors/disk_telemetry.hpp"
#include <windows.h>
#include <winioctl.h>
#include <ntddstor.h>
#include <nvme.h>
#include <array>
#include <cstring>
#include <algorithm>
#include <iostream>
#include <stdexcept>

namespace {
void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
}

int main()
{
    try {
        std::vector<std::byte> table;
        const auto record = [&](unsigned type, unsigned length) {
            const auto offset = table.size(); table.resize(offset + length + 2);
            table[offset] = static_cast<std::byte>(type); table[offset + 1] = static_cast<std::byte>(length);
            return offset;
        };
        const auto put = [&](std::size_t offset, unsigned value, unsigned size = 2) {
            for (unsigned i = 0; i < size; ++i) table[offset + i] = static_cast<std::byte>((value >> (8 * i)) & 255);
        };
        auto array = record(16, 15); put(array + 2, 1); put(array + 5, 3, 1); put(array + 13, 4);
        auto module = record(17, 34); put(module + 4, 1); put(module + 12, 8192); put(module + 14, 9, 1); put(module + 18, 26, 1); put(module + 32, 3200);
        auto empty = record(17, 34); put(empty + 4, 1);
        auto video = record(16, 15); put(video + 2, 2); put(video + 5, 4, 1); put(video + 13, 8);
        auto vram = record(17, 34); put(vram + 4, 2); put(vram + 12, 4096); put(vram + 32, 9000);
        record(127, 4);
        const auto facts = nks::parseMemoryHardware(table);
        const auto value = [&](const wchar_t* name) {
            const auto found = std::find_if(facts.begin(), facts.end(), [&](const auto& fact) { return fact.name == name; });
            return found == facts.end() ? L"" : found->value;
        };
        require(value(L"Použité sloty") == L"1 / 4" && value(L"Rychlost RAM") == L"3200 MT/s" && value(L"Typ paměti") == L"DDR4", "SMBIOS merged video RAM, empty slots or wrong speed");
        require(value(L"Provedení") == L"DIMM", "SMBIOS form factor was confused with the WMI enumeration");
        for (const auto& [code, expected] : std::vector<std::pair<unsigned, std::wstring>>{{12, L"RIMM"}, {13, L"SODIMM"}, {18, L"CUDIMM"}}) {
            put(module + 14, code, 1);
            const auto form = nks::parseMemoryHardware(table);
            require(std::any_of(form.begin(), form.end(), [&](const auto& fact) { return fact.name == L"Provedení" && fact.value == expected; }), "SMBIOS module form factor was misidentified");
        }
        put(module + 32, 0);
        const auto unknown = nks::parseMemoryHardware(table);
        require(std::any_of(unknown.begin(), unknown.end(), [](const auto& fact) { return fact.name == L"Rychlost RAM" && fact.value == L"Firmware nehlásí"; }), "Unknown RAM speed became zero");
        table.pop_back(); bool rejected = false;
        try { (void)nks::parseMemoryHardware(table); } catch (const std::runtime_error&) { rejected = true; }
        require(rejected, "Truncated firmware table was accepted");
        require(!nks::memoryHardware().empty() && !nks::processorHardware().empty(), "Live hardware metadata could not be read");
        using nks::VirtualizationProbe; using nks::virtualizationState;
        require(virtualizationState({true, true, true}) == L"Povoleno (Hyper-V)" && virtualizationState({true, true, false}) == L"Virtuální počítač"
            && virtualizationState({true, false, false, true, true}) == L"Virtuální počítač", "A hypervisor partition was misclassified");
        require(virtualizationState({false, false, false, true, true}) == L"Povoleno"
            && virtualizationState({false, false, false, true, false, true, true}) == L"Zakázáno ve firmwaru"
            && virtualizationState({false, false, false, true, false, false, true}) == L"Nepodporováno"
            && virtualizationState({false, false, false, false, true, true, true}) == L"Nepodporováno", "Bare-metal virtualization state differs from Task Manager's order");
        const auto live = nks::processorHardware();
        const auto state = std::find_if(live.begin(), live.end(), [](const auto& fact) { return fact.name == L"Virtualizace"; });
        require(state != live.end(), "Live processor facts have no virtualization state");
        std::string utf8(static_cast<std::size_t>(WideCharToMultiByte(CP_UTF8, 0, state->value.c_str(), -1, nullptr, 0, nullptr, nullptr)), '\0');
        WideCharToMultiByte(CP_UTF8, 0, state->value.c_str(), -1, utf8.data(), static_cast<int>(utf8.size()), nullptr, nullptr);
        std::cout << "Virtualization: " << utf8.c_str() << '\n';
        std::array<std::byte, sizeof(STORAGE_PROTOCOL_DATA_DESCRIPTOR) + sizeof(NVME_HEALTH_INFO_LOG)> health{};
        STORAGE_PROTOCOL_DATA_DESCRIPTOR header{};
        header.Version = header.Size = sizeof(header);
        header.ProtocolSpecificData.ProtocolType = ProtocolTypeNvme;
        header.ProtocolSpecificData.DataType = NVMeDataTypeLogPage;
        header.ProtocolSpecificData.ProtocolDataOffset = sizeof(STORAGE_PROTOCOL_SPECIFIC_DATA);
        header.ProtocolSpecificData.ProtocolDataLength = sizeof(NVME_HEALTH_INFO_LOG);
        NVME_HEALTH_INFO_LOG log{};
        log.Temperature[0] = 0x2F; log.Temperature[1] = 1;
        log.MediaErrors[8] = 1;
        std::fill(std::begin(log.PowerCycle), std::end(log.PowerCycle), UCHAR{255});
        log.PercentageUsed = 125;
        const auto encode = [&] {
            std::memcpy(health.data(), &header, sizeof(header));
            std::memcpy(health.data() + sizeof(header), &log, sizeof(log));
        };
        encode();
        const auto has = [&](const wchar_t* name, const wchar_t* expected) {
            const auto decoded = nks::decodeNvmeHealth(health);
            return std::any_of(decoded.begin(), decoded.end(), [&](const auto& item) { return item.name == name && item.value == expected; });
        };
        require(has(L"Teplota", L"29,9 °C"), "NVMe Kelvin temperature was not converted to Celsius");
        require(has(L"Chyby média", L"18446744073709551616"), "NVMe lifetime counter overflowed 64 bits");
        require(has(L"Zapnutí", L"340282366920938463463374607431768211455"), "NVMe 128-bit counter was truncated");
        require(has(L"Využitá životnost", L"125 %"), "NVMe wear above the rated lifetime was hidden");
        log.CriticalWarning.AsUchar = 4; log.Temperature[0] = log.Temperature[1] = 0; encode();
        require(has(L"SMART", L"Upozornění 0x4") && has(L"Teplota", L"Měření nedostupné"), "NVMe warning or unknown temperature became healthy zero");
        require(nks::decodeNvmeHealth(std::span(health).first(health.size() - 1)).empty(), "Truncated NVMe log was accepted");
        header.ProtocolSpecificData.ProtocolDataOffset = MAXDWORD; encode();
        require(nks::decodeNvmeHealth(health).empty(), "NVMe offset overflow was accepted");
        header.ProtocolSpecificData.ProtocolDataOffset = sizeof(STORAGE_PROTOCOL_SPECIFIC_DATA) - 1; encode();
        require(nks::decodeNvmeHealth(health).empty(), "NVMe payload overlapping the header was accepted");
        header.ProtocolSpecificData.ProtocolDataOffset = sizeof(STORAGE_PROTOCOL_SPECIFIC_DATA);
        header.ProtocolSpecificData.ProtocolType = ProtocolTypeAta; encode();
        require(nks::decodeNvmeHealth(health).empty(), "ATA vendor data was interpreted as NVMe health");
        std::cout << "SMBIOS memory, empty slots, video RAM exclusion and live topology passed\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
