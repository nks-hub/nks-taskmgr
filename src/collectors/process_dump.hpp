#pragma once
#include "process_identity.hpp"
#include "dump_worker/protocol.hpp"
#include <array>
#include <memory>
#include <string>
#include <vector>

namespace nks::dump {
struct HandleCloser { void operator()(HANDLE value) const noexcept { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); } };
using Handle = std::unique_ptr<void, HandleCloser>;
struct Request {
    Mode mode{};
    ProcessIdentity key;
    std::uint64_t created100ns{}, outputHandle{}, limitBytes{};
    DWORD parentPid{};
    std::wstring destination, temporary;
};
struct Result {
    State state{State::error};
    DWORD error{}, captureFlags{};
    Architecture architecture{Architecture::unknown};
    std::uint64_t fileBytes{}, observedCreated100ns{};
    std::array<std::byte, 32> sha256{};
    FILE_ID_INFO fileId{};
};
struct Context {
    Handle parent, process, file;
    ProcessIdentity parentKey;
    std::vector<std::byte> user;
    DWORD session{};
};
DWORD dumpError() noexcept;
bool prepareDump(const Request& request, Context& context, Result& result);
bool verifyDumpProcess(const Request& request, HANDLE process, Result& result);
bool dumpParentAlive(const Context& context);
Result captureDump(const Request& request);
}
