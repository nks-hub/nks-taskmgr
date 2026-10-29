#include <phnt_windows.h>
#include <phnt.h>
#include "collectors/boot_identity.hpp"
#include "collectors/process_collector.hpp"
#include <cstring>
#include <iostream>
#include <stdexcept>

namespace {
void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
enum class Response { valid, denied, privilege, unsupported, truncated, excessive, empty } response;
LONG NTAPI query(ULONG informationClass, void* buffer, ULONG size, ULONG* used)
{
    require(informationClass == SystemBootEnvironmentInformation && size == sizeof(SYSTEM_BOOT_ENVIRONMENT_INFORMATION), "Wrong boot query layout");
    SYSTEM_BOOT_ENVIRONMENT_INFORMATION raw{};
    const nks::BootIdentifier id{0x1020304050607080, 0x9080706050403020};
    if (response != Response::empty) std::memcpy(&raw.BootIdentifier, &id, sizeof(id));
    std::memcpy(buffer, &raw, sizeof(raw));
    *used = response == Response::truncated ? sizeof(raw) - 1 : response == Response::excessive ? sizeof(raw) + 1 : sizeof(raw);
    if (response == Response::denied) return STATUS_ACCESS_DENIED;
    if (response == Response::privilege) return STATUS_PRIVILEGE_NOT_HELD;
    if (response == Response::unsupported) return STATUS_INVALID_INFO_CLASS;
    return STATUS_SUCCESS;
}
}

int main()
{
    try {
        response = Response::valid;
        auto boot = nks::queryBootIdentity(query);
        require(boot.state == nks::SampleState::valid && boot.id == nks::BootIdentifier{0x1020304050607080, 0x9080706050403020}, "Boot GUID lost bits");
        for (const auto& [mode, expected] : {
            std::pair{Response::denied, nks::SampleState::accessDenied}, {Response::privilege, nks::SampleState::accessDenied},
            {Response::unsupported, nks::SampleState::unsupported}, {Response::truncated, nks::SampleState::error},
            {Response::excessive, nks::SampleState::error}, {Response::empty, nks::SampleState::error}}) {
            response = mode; boot = nks::queryBootIdentity(query);
            require(boot.state == expected && boot.id.empty(), "Failed boot query exposed an identity");
        }
        require(nks::queryBootIdentity(nullptr).state == nks::SampleState::error, "Missing boot query was accepted");
        nks::ProcessCollector first, second;
        const auto a = first.collect(), b = second.collect();
        require(a.boot.state == nks::SampleState::valid && b.boot.state == nks::SampleState::valid && !a.boot.id.empty()
            && a.boot.id == b.boot.id, "Independent collectors did not read the same boot GUID");
        for (const auto& process : a.processes) require(process.identity.boot == a.boot.id, "Process did not inherit its snapshot boot identity");
        std::cout << "Boot GUID, denied queries, bounded replies and independent live collectors passed\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
