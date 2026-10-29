#include "core/hardware.hpp"
#include <windows.h>
#include <winioctl.h>
#include <setupapi.h>
#include <initguid.h>
#include <poclass.h>
#include <array>
#include <algorithm>
#include <memory>

namespace nks {
namespace {
struct CloseHandleDeleter { void operator()(void* value) const { CloseHandle(value); } };
using DeviceHandle = std::unique_ptr<void, CloseHandleDeleter>;
}

std::vector<HardwareFact> upsHardware()
{
    const auto devices = SetupDiGetClassDevsW(&GUID_DEVICE_BATTERY, nullptr, nullptr, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
    if (devices == INVALID_HANDLE_VALUE) return {{L"Dostupnost", L"Ovladač UPS není dostupný"}};
    std::vector<HardwareFact> facts;
    unsigned found = 0, unreadable = 0;
    for (DWORD index = 0; index < 64; ++index) {
        SP_DEVICE_INTERFACE_DATA entry{sizeof(entry)};
        if (!SetupDiEnumDeviceInterfaces(devices, nullptr, &GUID_DEVICE_BATTERY, index, &entry)) {
            if (GetLastError() != ERROR_NO_MORE_ITEMS) ++unreadable;
            break;
        }
        DWORD size = 0; SetupDiGetDeviceInterfaceDetailW(devices, &entry, nullptr, 0, &size, nullptr);
        if (size < sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W) || size > 64 * 1024) { ++unreadable; continue; }
        std::vector<std::byte> buffer(size);
        auto* detail = reinterpret_cast<SP_DEVICE_INTERFACE_DETAIL_DATA_W*>(buffer.data()); detail->cbSize = sizeof(*detail);
        if (!SetupDiGetDeviceInterfaceDetailW(devices, &entry, detail, size, nullptr, nullptr)) { ++unreadable; continue; }
        const auto raw = CreateFileW(detail->DevicePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
        if (raw == INVALID_HANDLE_VALUE) { ++unreadable; continue; }
        DeviceHandle device(raw);
        ULONG wait = 0, tag = 0; DWORD returned = 0;
        if (!DeviceIoControl(raw, IOCTL_BATTERY_QUERY_TAG, &wait, sizeof(wait), &tag, sizeof(tag), &returned, nullptr) || returned < sizeof(tag) || !tag) { ++unreadable; continue; }
        BATTERY_QUERY_INFORMATION query{tag, BatteryInformation, 0}; BATTERY_INFORMATION info{};
        if (!DeviceIoControl(raw, IOCTL_BATTERY_QUERY_INFORMATION, &query, sizeof(query), &info, sizeof(info), &returned, nullptr) || returned < sizeof(info)) { ++unreadable; continue; }
        if (!(info.Capabilities & BATTERY_IS_SHORT_TERM)) continue;
        ++found;
        const std::wstring prefix = found == 1 ? L"" : L"UPS " + std::to_wstring(found) + L" · ";
        const auto fact = [&](const wchar_t* name, std::wstring value) { facts.push_back({prefix + name, std::move(value)}); };
        const auto text = [&](BATTERY_QUERY_INFORMATION_LEVEL level, const wchar_t* label) {
            std::array<wchar_t, 1024> value{}; query.InformationLevel = level;
            if (DeviceIoControl(raw, IOCTL_BATTERY_QUERY_INFORMATION, &query, sizeof(query), value.data(), static_cast<DWORD>(value.size() * sizeof(wchar_t)), &returned, nullptr)
                && returned >= sizeof(wchar_t) && returned <= value.size() * sizeof(wchar_t)) {
                value.back() = 0; if (value[0]) fact(label, value.data());
            }
        };
        text(BatteryDeviceName, L"Model"); text(BatteryManufactureName, L"Výrobce");
        BATTERY_WAIT_STATUS wanted{tag, 0, 0, 0, 0}; BATTERY_STATUS status{};
        if (DeviceIoControl(raw, IOCTL_BATTERY_QUERY_STATUS, &wanted, sizeof(wanted), &status, sizeof(status), &returned, nullptr) && returned >= sizeof(status)) {
            fact(L"Napájení", status.PowerState & BATTERY_POWER_ON_LINE ? L"Elektrická síť" : status.PowerState & BATTERY_DISCHARGING ? L"Baterie" : L"Neznámé");
            fact(L"Stav baterie", status.PowerState & BATTERY_CRITICAL ? L"Kritická" : status.PowerState & BATTERY_CHARGING ? L"Nabíjí se" : L"Bez nabíjení");
            if (status.Capacity != BATTERY_UNKNOWN_CAPACITY && info.FullChargedCapacity && info.FullChargedCapacity != BATTERY_UNKNOWN_CAPACITY)
                fact(L"Nabití", std::to_wstring(std::min(100ull, static_cast<unsigned long long>(status.Capacity) * 100 / info.FullChargedCapacity)) + L" %");
            if (status.Voltage != BATTERY_UNKNOWN_VOLTAGE) fact(L"Napětí baterie", std::to_wstring(status.Voltage / 1000.) + L" V");
            if (!(info.Capabilities & BATTERY_CAPACITY_RELATIVE) && status.Rate != BATTERY_UNKNOWN_RATE) fact(L"Tok energie baterie", std::to_wstring(status.Rate / 1000.) + L" W");
        } else fact(L"Stav", L"Měření nedostupné");
        query.InformationLevel = BatteryEstimatedTime; ULONG remaining = BATTERY_UNKNOWN_TIME;
        if (DeviceIoControl(raw, IOCTL_BATTERY_QUERY_INFORMATION, &query, sizeof(query), &remaining, sizeof(remaining), &returned, nullptr) && returned >= sizeof(remaining) && remaining != BATTERY_UNKNOWN_TIME)
            fact(L"Odhad výdrže", std::to_wstring(remaining / 60) + L" min");
    }
    SetupDiDestroyDeviceInfoList(devices);
    if (!found) facts.push_back({L"Dostupnost", unreadable ? L"Zařízení nelze přečíst" : L"Windows nehlásí podporovanou UPS"});
    return facts;
}
}
