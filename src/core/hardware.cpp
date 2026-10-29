#include "hardware.hpp"
#include <algorithm>
#include <cstdint>
#include <map>
#include <set>
#include <stdexcept>

namespace nks {
namespace {
std::uint32_t number(std::span<const std::byte> data, std::size_t at, std::size_t size)
{
    if (at > data.size() || size > data.size() - at) throw std::runtime_error("Truncated SMBIOS field");
    std::uint32_t value = 0;
    for (std::size_t i = 0; i < size; ++i) value |= std::to_integer<std::uint32_t>(data[at + i]) << (8 * i);
    return value;
}
}

std::vector<HardwareFact> parseMemoryHardware(std::span<const std::byte> table)
{
    std::vector<std::span<const std::byte>> records;
    for (std::size_t offset = 0; offset < table.size();) {
        if (table.size() - offset < 4) throw std::runtime_error("Truncated SMBIOS record");
        const auto length = number(table, offset + 1, 1);
        if (length < 4 || length > table.size() - offset) throw std::runtime_error("Invalid SMBIOS record length");
        auto record = table.subspan(offset, length);
        std::size_t end = offset + length;
        while (end + 1 < table.size() && (table[end] != std::byte{} || table[end + 1] != std::byte{})) ++end;
        if (end + 1 >= table.size()) throw std::runtime_error("Unterminated SMBIOS strings");
        records.push_back(record); offset = end + 2;
        if (number(record, 0, 1) == 127) break;
    }
    std::set<unsigned> arrays;
    unsigned slots = 0, used = 0;
    for (const auto record : records) if (number(record, 0, 1) == 16 && record.size() >= 15 && number(record, 5, 1) == 3) {
        arrays.insert(number(record, 2, 2)); slots += number(record, 13, 2);
    }
    std::set<unsigned> rates, types, forms;
    bool unknownRate = false;
    for (const auto record : records) {
        if (number(record, 0, 1) != 17 || record.size() < 21 || !arrays.contains(number(record, 4, 2)) || number(record, 12, 2) == 0) continue;
        ++used; types.insert(number(record, 18, 1)); forms.insert(number(record, 14, 1));
        unsigned rate = record.size() >= 34 ? number(record, 32, 2) : 0;
        if (rate == 0xffff) rate = record.size() >= 92 ? number(record, 88, 4) : 0;
        if (!rate) unknownRate = true; else rates.insert(rate);
    }
    std::vector<HardwareFact> result;
    if (slots) result.push_back({L"Použité sloty", std::to_wstring(used) + L" z " + std::to_wstring(slots)});
    if (!rates.empty() && !unknownRate) result.push_back({L"Rychlost RAM", std::to_wstring(*rates.begin())
        + (rates.size() > 1 ? L"–" + std::to_wstring(*rates.rbegin()) : L"") + L" MT/s"});
    else result.push_back({L"Rychlost RAM", L"Firmware nehlásí"});
    const std::map<unsigned, std::wstring> names{{24, L"DDR3"}, {26, L"DDR4"}, {27, L"LPDDR"}, {28, L"LPDDR2"}, {29, L"LPDDR3"}, {30, L"LPDDR4"}, {34, L"DDR5"}, {35, L"LPDDR5"}};
    if (types.size() == 1 && names.contains(*types.begin())) result.push_back({L"Typ paměti", names.at(*types.begin())});
    if (forms.size() == 1 && (*forms.begin() == 8 || *forms.begin() == 12)) result.push_back({L"Provedení", *forms.begin() == 8 ? L"DIMM" : L"SODIMM"});
    return result;
}
}
