#include "core/hardware.hpp"
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
        auto module = record(17, 34); put(module + 4, 1); put(module + 12, 8192); put(module + 14, 8, 1); put(module + 18, 26, 1); put(module + 32, 3200);
        auto empty = record(17, 34); put(empty + 4, 1);
        auto video = record(16, 15); put(video + 2, 2); put(video + 5, 4, 1); put(video + 13, 8);
        auto vram = record(17, 34); put(vram + 4, 2); put(vram + 12, 4096); put(vram + 32, 9000);
        record(127, 4);
        const auto facts = nks::parseMemoryHardware(table);
        const auto value = [&](const wchar_t* name) {
            const auto found = std::find_if(facts.begin(), facts.end(), [&](const auto& fact) { return fact.name == name; });
            return found == facts.end() ? L"" : found->value;
        };
        require(value(L"Použité sloty") == L"1 z 4" && value(L"Rychlost RAM") == L"3200 MT/s" && value(L"Typ paměti") == L"DDR4", "SMBIOS merged video RAM, empty slots or wrong speed");
        put(module + 32, 0);
        const auto unknown = nks::parseMemoryHardware(table);
        require(std::any_of(unknown.begin(), unknown.end(), [](const auto& fact) { return fact.name == L"Rychlost RAM" && fact.value == L"Firmware nehlásí"; }), "Unknown RAM speed became zero");
        table.pop_back(); bool rejected = false;
        try { (void)nks::parseMemoryHardware(table); } catch (const std::runtime_error&) { rejected = true; }
        require(rejected, "Truncated firmware table was accepted");
        require(!nks::memoryHardware().empty() && !nks::processorHardware().empty(), "Live hardware metadata could not be read");
        std::cout << "SMBIOS memory, empty slots, video RAM exclusion and live topology passed\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
