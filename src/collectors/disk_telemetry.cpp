#include "disk_telemetry.hpp"
#include <windows.h>
#include <winioctl.h>
#include <ntddstor.h>
#include <nvme.h>
#include <algorithm>
#include <array>
#include <cstring>
#include <iomanip>
#include <sstream>

namespace nks {
namespace {
class DiskHandle {
public:
    explicit DiskHandle(const std::wstring& path) : value(CreateFileW(path.c_str(), 0,
        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr)) {}
    ~DiskHandle() { if (value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    HANDLE value;
};

std::wstring number(double value, const wchar_t* unit, int precision = 1)
{
    std::wostringstream text;
    text << std::fixed << std::setprecision(precision) << value << unit;
    auto result = text.str();
    std::replace(result.begin(), result.end(), L'.', L',');
    return result;
}

std::wstring decimal128(const UCHAR* bytes)
{
    std::wstring result = L"0";
    for (int i = 15; i >= 0; --i) {
        unsigned carry = bytes[i];
        for (auto digit = result.rbegin(); digit != result.rend(); ++digit) {
            carry += static_cast<unsigned>(*digit - L'0') * 256;
            *digit = static_cast<wchar_t>(L'0' + carry % 10);
            carry /= 10;
        }
        while (carry) { result.insert(result.begin(), static_cast<wchar_t>(L'0' + carry % 10)); carry /= 10; }
    }
    return result;
}

double dataUnits(const UCHAR* bytes)
{
    double result = 0;
    for (int i = 15; i >= 0; --i) result = result * 256 + bytes[i];
    return result * 512000. / 1099511627776.;
}

std::wstring temperature(unsigned kelvin)
{
    return kelvin == 0 || kelvin == 65535 ? L"Měření nedostupné" : number(static_cast<double>(kelvin) - 273.15, L" °C");
}

std::wstring failure(DWORD error)
{
    return error == ERROR_ACCESS_DENIED || error == ERROR_PRIVILEGE_NOT_HELD ? L"Chybí oprávnění"
        : error == ERROR_NOT_SUPPORTED || error == ERROR_INVALID_FUNCTION || error == ERROR_INVALID_PARAMETER ? L"Nepodporováno ovladačem" : L"Měření nedostupné";
}

std::wstring busName(STORAGE_BUS_TYPE bus)
{
    switch (bus) {
    case BusTypeNvme: return L"NVMe";
    case BusTypeSata: return L"SATA";
    case BusTypeAta: return L"ATA";
    case BusTypeUsb: return L"USB";
    case BusTypeSas: return L"SAS";
    case BusTypeScsi: return L"SCSI";
    case BusTypeRAID: return L"RAID";
    case BusTypeiScsi: return L"iSCSI";
    case BusTypeVirtual: case BusTypeFileBackedVirtual: return L"Virtuální";
    default: return L"Windows nehlásí";
    }
}

std::vector<HardwareFact> health(HANDLE handle)
{
    constexpr DWORD length = sizeof(STORAGE_PROTOCOL_DATA_DESCRIPTOR) + sizeof(NVME_HEALTH_INFO_LOG);
    std::array<std::byte, length> buffer{};
    STORAGE_PROPERTY_QUERY query{};
    query.PropertyId = StorageDeviceProtocolSpecificProperty;
    query.QueryType = PropertyStandardQuery;
    STORAGE_PROTOCOL_SPECIFIC_DATA protocol{};
    protocol.ProtocolType = ProtocolTypeNvme;
    protocol.DataType = NVMeDataTypeLogPage;
    protocol.ProtocolDataRequestValue = NVME_LOG_PAGE_HEALTH_INFO;
    protocol.ProtocolDataOffset = sizeof(protocol);
    protocol.ProtocolDataLength = sizeof(NVME_HEALTH_INFO_LOG);
    std::memcpy(buffer.data(), &query, offsetof(STORAGE_PROPERTY_QUERY, AdditionalParameters));
    std::memcpy(buffer.data() + offsetof(STORAGE_PROPERTY_QUERY, AdditionalParameters), &protocol, sizeof(protocol));
    DWORD used = 0;
    if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, buffer.data(), length,
        buffer.data(), length, &used, nullptr) && used <= buffer.size()) {
        auto facts = decodeNvmeHealth(std::span(buffer).first(used));
        if (!facts.empty()) return facts;
    }
    STORAGE_PREDICT_FAILURE prediction{};
    if (DeviceIoControl(handle, IOCTL_STORAGE_PREDICT_FAILURE, nullptr, 0, &prediction, sizeof(prediction), &used, nullptr)
        && used >= sizeof(prediction))
        return {{L"SMART", prediction.PredictFailure ? L"Disk předpovídá selhání" : L"Selhání není hlášeno"}};
    return {{L"SMART", failure(GetLastError())}};
}
}

std::vector<HardwareFact> decodeNvmeHealth(std::span<const std::byte> descriptor)
{
    STORAGE_PROTOCOL_DATA_DESCRIPTOR header{};
    if (descriptor.size() < sizeof(header)) return {};
    std::memcpy(&header, descriptor.data(), sizeof(header));
    const auto& protocol = header.ProtocolSpecificData;
    constexpr auto base = offsetof(STORAGE_PROTOCOL_DATA_DESCRIPTOR, ProtocolSpecificData);
    if (header.Version != sizeof(header) || header.Size != sizeof(header) || protocol.ProtocolType != ProtocolTypeNvme
        || protocol.DataType != NVMeDataTypeLogPage || protocol.ProtocolDataOffset < sizeof(protocol)
        || protocol.ProtocolDataOffset > descriptor.size() - base || protocol.ProtocolDataLength < sizeof(NVME_HEALTH_INFO_LOG)
        || protocol.ProtocolDataLength > descriptor.size() - base - protocol.ProtocolDataOffset) return {};
    NVME_HEALTH_INFO_LOG log{};
    std::memcpy(&log, descriptor.data() + base + protocol.ProtocolDataOffset, sizeof(log));
    std::wostringstream warning;
    warning << L"Upozornění 0x" << std::hex << std::uppercase << static_cast<unsigned>(log.CriticalWarning.AsUchar);
    std::vector<HardwareFact> facts{
        {L"SMART", log.CriticalWarning.AsUchar ? warning.str() : L"Bez kritického upozornění"},
        {L"Zdroj stavu", L"NVMe SMART / Health"},
        {L"Teplota", temperature(static_cast<unsigned>(log.Temperature[0]) | (static_cast<unsigned>(log.Temperature[1]) << 8))},
        {L"Využitá životnost", std::to_wstring(log.PercentageUsed) + L" %"},
        {L"Provozní hodiny", decimal128(log.PowerOnHours) + L" h"},
        {L"Zapnutí", decimal128(log.PowerCycle)},
        {L"Nečistá vypnutí", decimal128(log.UnsafeShutdowns)},
        {L"Chyby média", decimal128(log.MediaErrors)},
        {L"Přečteno celkem", number(dataUnits(log.DataUnitRead), L" TiB")},
        {L"Zapsáno celkem", number(dataUnits(log.DataUnitWritten), L" TiB")}
    };
    if (log.AvailableSpare <= 100) facts.push_back({L"Rezervní kapacita", std::to_wstring(log.AvailableSpare) + L" %"});
    return facts;
}

std::vector<HardwareFact> diskHardware(const std::wstring& path)
{
    DiskHandle handle(path);
    if (handle.value == INVALID_HANDLE_VALUE) return {{L"Parametry disku", failure(GetLastError())}};
    std::vector<HardwareFact> facts;
    STORAGE_PROPERTY_QUERY query{};
    query.PropertyId = StorageDeviceProperty;
    query.QueryType = PropertyStandardQuery;
    std::array<std::byte, 4096> buffer{};
    DWORD used = 0;
    if (DeviceIoControl(handle.value, IOCTL_STORAGE_QUERY_PROPERTY, &query, sizeof(query), buffer.data(),
        static_cast<DWORD>(buffer.size()), &used, nullptr) && used >= sizeof(STORAGE_DEVICE_DESCRIPTOR)) {
        STORAGE_DEVICE_DESCRIPTOR description{};
        std::memcpy(&description, buffer.data(), sizeof(description));
        facts.push_back({L"Rozhraní", busName(description.BusType)});
    }
    DISK_GEOMETRY_EX geometry{};
    if (DeviceIoControl(handle.value, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, nullptr, 0, &geometry, sizeof(geometry), &used, nullptr)
        && used >= offsetof(DISK_GEOMETRY_EX, Data) && geometry.DiskSize.QuadPart > 0)
        facts.push_back({L"Kapacita disku", number(static_cast<double>(geometry.DiskSize.QuadPart) / 1073741824., L" GiB")});
    query.PropertyId = StorageAccessAlignmentProperty;
    STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR alignment{};
    if (DeviceIoControl(handle.value, IOCTL_STORAGE_QUERY_PROPERTY, &query, sizeof(query), &alignment, sizeof(alignment), &used, nullptr)
        && used >= sizeof(alignment) && alignment.BytesPerLogicalSector)
        facts.push_back({L"Sektor logický / fyzický", std::to_wstring(alignment.BytesPerLogicalSector) + L" / "
            + (alignment.BytesPerPhysicalSector ? std::to_wstring(alignment.BytesPerPhysicalSector) : L"?") + L" B"});
    return facts;
}

std::vector<HardwareFact> diskHealth(const std::wstring& path)
{
    DiskHandle handle(path);
    if (handle.value == INVALID_HANDLE_VALUE) {
        const auto message = failure(GetLastError());
        return {{L"SMART", message}, {L"Teplota", message}};
    }
    auto facts = health(handle.value);
    if (std::none_of(facts.begin(), facts.end(), [](const auto& item) { return item.name == L"Teplota"; })) {
        STORAGE_PROPERTY_QUERY query{};
        query.PropertyId = StorageDeviceTemperatureProperty;
        query.QueryType = PropertyStandardQuery;
        std::array<std::byte, 4096> buffer{};
        DWORD used = 0;
        std::wstring value = L"Měření nedostupné";
        if (DeviceIoControl(handle.value, IOCTL_STORAGE_QUERY_PROPERTY, &query, sizeof(query), buffer.data(),
            static_cast<DWORD>(buffer.size()), &used, nullptr) && used <= buffer.size() && used >= sizeof(STORAGE_TEMPERATURE_DATA_DESCRIPTOR)) {
            STORAGE_TEMPERATURE_DATA_DESCRIPTOR descriptor{};
            std::memcpy(&descriptor, buffer.data(), sizeof(descriptor));
            constexpr auto offset = offsetof(STORAGE_TEMPERATURE_DATA_DESCRIPTOR, TemperatureInfo);
            if (descriptor.InfoCount && descriptor.InfoCount <= (used - offset) / sizeof(STORAGE_TEMPERATURE_INFO)) {
                STORAGE_TEMPERATURE_INFO sensor{};
                std::memcpy(&sensor, buffer.data() + offset, sizeof(sensor));
                if (static_cast<USHORT>(sensor.Temperature) != STORAGE_TEMPERATURE_VALUE_NOT_REPORTED)
                    value = number(sensor.Temperature, L" °C");
            }
        } else value = failure(GetLastError());
        facts.push_back({L"Teplota", value});
    }
    facts.push_back({L"Obnova SMART / teploty", L"30 s"});
    return facts;
}
}
