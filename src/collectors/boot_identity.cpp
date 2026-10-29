#include <phnt_windows.h>
#include <phnt.h>
#include "boot_identity.hpp"
#include <cstring>

namespace nks {
BootIdentity queryBootIdentity(NKS_SYSTEM_QUERY query)
{
    if (!query) return {{}, SampleState::error};
    SYSTEM_BOOT_ENVIRONMENT_INFORMATION environment{};
    ULONG returned = 0;
    const auto status = query(SystemBootEnvironmentInformation, &environment, sizeof(environment), &returned);
    if (status == STATUS_ACCESS_DENIED || status == STATUS_PRIVILEGE_NOT_HELD) return {{}, SampleState::accessDenied};
    if (status == STATUS_INVALID_INFO_CLASS || status == STATUS_NOT_IMPLEMENTED || status == STATUS_NOT_SUPPORTED)
        return {{}, SampleState::unsupported};
    if (!NT_SUCCESS(status) || returned != sizeof(environment)) return {{}, SampleState::error};
    static_assert(sizeof(BootIdentifier) == sizeof(GUID));
    BootIdentifier id;
    std::memcpy(&id, &environment.BootIdentifier, sizeof(id));
    return id.empty() ? BootIdentity{{}, SampleState::error} : BootIdentity{id, SampleState::valid};
}
}
