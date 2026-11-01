#pragma once

#include "process_identity.hpp"
#include "details_worker/protocol.hpp"
#include <memory>
#include <optional>

namespace nks {

struct ProcessDetailsRequest {
    ProcessIdentity target;
    details::Command command{};
    std::uint64_t moduleBase{};
    std::wstring modulePath;
};

struct ProcessModuleDetail {
    std::uint64_t base{}, imageSize{};
    std::wstring name, path;
};

struct ProcessFileSignature {
    details::SignatureState state{details::SignatureState::unavailable};
    details::SignatureSource source{details::SignatureSource::none};
    LONG trustCode{};
    std::wstring path, publisher;
};

struct ProcessDetailsResult {
    details::ResultState state{details::ResultState::unavailable};
    DWORD win32Error{};
    LONG ntStatus{};
    details::Architecture architecture{};
    std::uint32_t flags{};
    std::vector<details::Thread> threads;
    std::vector<ProcessModuleDetail> modules;
    std::optional<ProcessFileSignature> signature;
};

struct DetailHandleCloser {
    void operator()(HANDLE handle) const noexcept { if (handle && handle != INVALID_HANDLE_VALUE) CloseHandle(handle); }
};
using DetailHandle = std::unique_ptr<void, DetailHandleCloser>;

ProcessDetailsResult readProcessDetails(const ProcessDetailsRequest& request);
void readFileSignatureOnDisk(const std::wstring& path, ProcessDetailsResult& result);

}
