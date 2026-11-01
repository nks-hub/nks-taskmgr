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
    std::uint64_t handleValue{};
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

struct ProcessHandleDetail {
    details::HandleInfo info;
    std::wstring type, name;
};

struct ProcessOwnerDetail {
    ProcessIdentity identity;
    std::wstring name;
};

struct ProcessApplicationDetail {
    ProcessIdentity owner, host;
    std::uint64_t window{};
    std::wstring title, appId, package;
    SampleState appIdState{SampleState::unavailable}, packageState{SampleState::unavailable};
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
    std::vector<details::MemoryRegion> memory;
    std::vector<details::HandleEntry> handles;
    std::optional<ProcessHandleDetail> handleInfo;
    std::vector<ProcessOwnerDetail> owners;
    std::vector<ProcessApplicationDetail> applications;
};

struct DetailHandleCloser {
    void operator()(HANDLE handle) const noexcept { if (handle && handle != INVALID_HANDLE_VALUE) CloseHandle(handle); }
};
using DetailHandle = std::unique_ptr<void, DetailHandleCloser>;

ProcessDetailsResult readProcessDetails(const ProcessDetailsRequest& request);
void readFileSignatureOnDisk(const std::wstring& path, ProcessDetailsResult& result);
void readProcessMemory(HANDLE process, ProcessDetailsResult& result);
void readProcessHandles(HANDLE process, ProcessDetailsResult& result);
void readProcessHandleInfo(HANDLE process, std::uint64_t value, ProcessDetailsResult& result);
void readProcessOwners(const ProcessDetailsRequest& request, ProcessDetailsResult& result);
void readProcessApplications(const ProcessDetailsRequest& request, ProcessDetailsResult& result);

}
