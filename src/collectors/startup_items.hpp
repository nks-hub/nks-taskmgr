#pragma once

#include <windows.h>
#include <array>
#include <cstdint>
#include <span>
#include <string>
#include <string_view>
#include <vector>

namespace nks {

enum class StartupSource : std::uint32_t {
    userRun, userRunOnce, machineRun, machineRunOnce, machineRun32, machineRunOnce32, userFolder, commonFolder
};
enum class StartupState : std::uint32_t { enabled, disabled, runOnce };

struct StartupApproval {
    bool disabled{};
    std::uint64_t changed{};
};

struct StartupItem {
    StartupSource source{};
    StartupState state{};
    std::uint64_t changed{};
    std::wstring name, command, location, target;
};

/// Registry roots and startup folders read by the inventory; tests substitute a temporary HKCU subkey and folders.
struct StartupLocations {
    HKEY user{HKEY_CURRENT_USER}, machine{HKEY_LOCAL_MACHINE};
    std::wstring userFolder, commonFolder;
};

StartupLocations defaultStartupLocations();
/// Windows treats a missing or short StartupApproved value as enabled and an odd first byte as disabled.
StartupApproval parseStartupApproved(std::span<const std::uint8_t> value);
/// Builds the 12-byte value Windows writes: flag byte, three zero bytes, FILETIME of disabling (zero when enabled).
std::array<std::uint8_t, 12> startupApprovedValue(std::span<const std::uint8_t> previous, bool enabled, std::uint64_t now);
/// Extracts the executable from a Run command the way CreateProcess resolves an unquoted image path.
std::wstring startupTarget(std::wstring_view command);
std::vector<StartupItem> readStartupItems(const StartupLocations& locations);
/// Changes only current-user Run and Startup folder items that exist; never deletes anything.
DWORD setStartupItemEnabled(const StartupLocations& locations, StartupSource source, std::wstring_view name, bool enabled);

}
