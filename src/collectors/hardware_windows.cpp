#include "core/hardware.hpp"
#include <windows.h>
#include <cstring>
#include <sstream>
#include <iomanip>
#include <stdexcept>
#include <intrin.h>
#include <array>

namespace nks {

std::vector<HardwareFact> memoryHardware()
{
    constexpr DWORD rsmb = 0x52534d42;
    const auto size = GetSystemFirmwareTable(rsmb, 0, nullptr, 0);
    if (size < 8 || size > 16 * 1024 * 1024) return {{L"Údaje RAM", L"Firmware nedostupný"}};
    std::vector<std::byte> bytes(size);
    if (GetSystemFirmwareTable(rsmb, 0, bytes.data(), size) != size) return {{L"Údaje RAM", L"Čtení selhalo"}};
    DWORD length = 0; std::memcpy(&length, bytes.data() + 4, sizeof(length));
    if (length > size - 8) return {{L"Údaje RAM", L"Neplatný firmware"}};
    try { return parseMemoryHardware(std::span(bytes).subspan(8, length)); }
    catch (const std::runtime_error&) { return {{L"Údaje RAM", L"Neplatný firmware"}}; }
}

std::vector<HardwareFact> processorHardware()
{
    DWORD size = 0; GetLogicalProcessorInformationEx(RelationAll, nullptr, &size);
    if (!size || size > 16 * 1024 * 1024) return {};
    std::vector<std::byte> bytes(size);
    if (!GetLogicalProcessorInformationEx(RelationAll, reinterpret_cast<PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(bytes.data()), &size)) return {};
    unsigned cores = 0, packages = 0;
    unsigned long long caches[3]{};
    for (DWORD offset = 0; offset < size;) {
        if (size - offset < 8) return {};
        const auto* record = reinterpret_cast<const SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*>(bytes.data() + offset);
        if (record->Size < 8 || record->Size > size - offset) return {};
        if (record->Relationship == RelationProcessorCore) ++cores;
        if (record->Relationship == RelationProcessorPackage) ++packages;
        if (record->Relationship == RelationCache && record->Size >= offsetof(SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX, Cache) + sizeof(CACHE_RELATIONSHIP)
            && record->Cache.Level >= 1 && record->Cache.Level <= 3) caches[record->Cache.Level - 1] += record->Cache.CacheSize;
        offset += record->Size;
    }
    std::vector<HardwareFact> result{{L"Fyzická jádra", std::to_wstring(cores)}, {L"Patice", std::to_wstring(packages)}};
    for (unsigned i = 0; i < 3; ++i) if (caches[i]) result.push_back({L"Mezipaměť L" + std::to_wstring(i + 1), std::to_wstring(caches[i] / 1024) + L" KiB"});
    int info[4]{}; __cpuid(info, 0); const int maximumLeaf = info[0];
    __cpuid(info, 1);
    const unsigned eax = static_cast<unsigned>(info[0]), baseFamily = (eax >> 8) & 15, baseModel = (eax >> 4) & 15;
    const unsigned family = baseFamily == 15 ? baseFamily + ((eax >> 20) & 255) : baseFamily;
    const unsigned model = (baseFamily == 6 || baseFamily == 15) ? baseModel + (((eax >> 16) & 15) << 4) : baseModel;
    result.push_back({L"Rodina / model / revize", std::to_wstring(family) + L" / " + std::to_wstring(model) + L" / " + std::to_wstring(eax & 15)});
    std::wstring features;
    const auto feature = [&](bool present, const wchar_t* name) { if (present) { if (!features.empty()) features += L", "; features += name; } };
    feature((info[3] & (1 << 26)) != 0, L"SSE2"); feature((info[2] & (1 << 20)) != 0, L"SSE4.2");
    feature((info[2] & (1 << 25)) != 0, L"AES"); feature((info[2] & (1 << 28)) != 0, L"AVX");
    if (maximumLeaf >= 7) { __cpuidex(info, 7, 0); feature((info[1] & (1 << 5)) != 0, L"AVX2"); }
    if (!features.empty()) result.push_back({L"Instrukce CPU (CPUID)", features});
    if (maximumLeaf >= 0x16) {
        __cpuid(info, 0x16);
        if ((info[0] & 65535) > 0) result.push_back({L"Základní takt (CPUID)", std::to_wstring(info[0] & 65535) + L" MHz"});
        if ((info[1] & 65535) > 0) result.push_back({L"Maximum (CPUID)", std::to_wstring(info[1] & 65535) + L" MHz"});
    }
    return result;
}

std::vector<HardwareFact> boardHardware()
{
    std::vector<HardwareFact> facts;
    const auto read = [&](const wchar_t* field, const wchar_t* name) {
        std::array<wchar_t, 1024> value{}; DWORD size = static_cast<DWORD>(value.size() * sizeof(wchar_t));
        if (RegGetValueW(HKEY_LOCAL_MACHINE, L"HARDWARE\\DESCRIPTION\\System\\BIOS", field, RRF_RT_REG_SZ, nullptr, value.data(), &size) == ERROR_SUCCESS && value[0])
            facts.push_back({name, value.data()});
    };
    read(L"BaseBoardManufacturer", L"Výrobce desky"); read(L"BaseBoardProduct", L"Model desky"); read(L"BaseBoardVersion", L"Verze desky");
    read(L"BIOSVendor", L"Výrobce BIOS"); read(L"BIOSVersion", L"Verze BIOS"); read(L"BIOSReleaseDate", L"Datum BIOS");
    read(L"SystemManufacturer", L"Výrobce systému"); read(L"SystemProductName", L"Model systému");
    FIRMWARE_TYPE type{};
    if (GetFirmwareType(&type)) facts.push_back({L"Režim firmware", type == FirmwareTypeUefi ? L"UEFI" : type == FirmwareTypeBios ? L"BIOS" : L"Neznámý"});
    return facts;
}

std::wstring hardwareRate(unsigned long long bits)
{
    if (!bits) return L"Nehlášeno";
    const auto divisor = bits >= 1'000'000'000 ? 1'000'000'000. : 1'000'000.;
    std::wostringstream text; text << std::fixed << std::setprecision(bits % static_cast<unsigned long long>(divisor) ? 1 : 0) << bits / divisor;
    return text.str() + (bits >= 1'000'000'000 ? L" Gbit/s" : L" Mbit/s");
}
}
