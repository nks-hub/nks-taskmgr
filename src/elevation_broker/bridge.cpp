#include "transport.hpp"
#include "core/broker_pipe.hpp"
#include <shellapi.h>
#include <objbase.h>
#include <thread>
#include <atomic>

namespace nks::broker {
namespace {
struct Launch {
    std::wstring path, parameters;
    Handle done{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    Handle process;
    DWORD error{};
};
bool elevatedLaunch(const std::wstring& path, const std::wstring& pipe, Handle& process)
{
    auto launch = std::make_shared<Launch>(); launch->path = path;
    launch->parameters = L"--serve " + pipe.substr(pipe.size() - 32) + L" " + std::to_wstring(GetCurrentProcessId());
    if (!launch->done) return false;
    std::thread([launch] {
        const HRESULT apartment = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        if (FAILED(apartment)) { launch->error = ERROR_DLL_INIT_FAILED; SetEvent(launch->done.get()); return; }
        SHELLEXECUTEINFOW request{}; request.cbSize = sizeof(request);
        request.fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI;
        request.lpVerb = L"runas"; request.lpFile = launch->path.c_str(); request.lpParameters = launch->parameters.c_str();
        request.nShow = SW_HIDE;
        if (ShellExecuteExW(&request) && request.hProcess) launch->process.reset(request.hProcess);
        else launch->error = GetLastError() ? GetLastError() : ERROR_GEN_FAILURE;
        CoUninitialize(); SetEvent(launch->done.get());
    }).detach();
    if (WaitForSingleObject(launch->done.get(), static_cast<DWORD>(startupMs)) != WAIT_OBJECT_0) { SetLastError(ERROR_TIMEOUT); return false; }
    if (!launch->process) { SetLastError(launch->error); return false; }
    process = std::move(launch->process); return true;
}
Handle openServer(const std::wstring& name, HANDLE server, ULONGLONG deadline)
{
    while (GetTickCount64() < deadline && WaitForSingleObject(server, 0) == WAIT_TIMEOUT) {
        HANDLE raw = CreateFileW(name.c_str(), FILE_READ_DATA | FILE_WRITE_DATA | FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES
            | READ_CONTROL | SYNCHRONIZE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
        if (raw != INVALID_HANDLE_VALUE) return Handle(raw);
        const auto error = GetLastError();
        if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PIPE_BUSY) return {};
        // This wait only runs inside the private bridge, never on the GUI thread.
        if (!WaitNamedPipeW(name.c_str(), 100) && GetLastError() == ERROR_FILE_NOT_FOUND) WaitForSingleObject(server, 50);
    }
    SetLastError(ERROR_TIMEOUT); return {};
}
}

int bridge()
{
    using namespace actions; using namespace actions::wire;
    const HANDLE input = GetStdHandle(STD_INPUT_HANDLE), output = GetStdHandle(STD_OUTPUT_HANDLE);
    if (GetFileType(input) != FILE_TYPE_PIPE || GetFileType(output) != FILE_TYPE_PIPE || input == output) return 2;
    Header header{};
    if (!stdioIo(input, header, false) || !headerValid(header)) return 2;
    auto reject = [&](DWORD error) { stdioWrite(output, response(header, FrameKind::rejected, 0, error)); return 2; };
    std::vector<std::byte> payload(read32(header, 8)); std::vector<Result> targets;
    CpuSetsHash baseline{}; std::vector<std::uint32_t> selected;
    if (!stdioIo(input, payload, false) || !decodeTargets(header, payload, targets, baseline, selected)) return reject(ERROR_INVALID_PARAMETER);
    BYTE extra{}; DWORD size = 0; const BOOL end = ReadFile(input, &extra, 1, &size, nullptr);
    if (size || (!end && GetLastError() != ERROR_BROKEN_PIPE && GetLastError() != ERROR_HANDLE_EOF)) return reject(ERROR_INVALID_DATA);
    if (!trustConfigured()) return reject(trustNotConfigured);
    Peer self, gui;
    if (!verifyPeer(GetCurrentProcessId(), L"nks-elevation-broker.exe", self)
        || !verifyPeer(parentPid(GetCurrentProcess()), L"NksTaskMgr.WinUI.exe", gui)
        || !sameLogon(self.token, gui.token) || gui.key.boot != self.key.boot || gui.key.generation > self.key.generation) return reject(trustRejected);
    const auto name = newPipeName(); if (name.empty()) return reject(ERROR_GEN_FAILURE);
    Handle elevated;
    if (!elevatedLaunch(self.path, name, elevated)) return reject(GetLastError());
    const auto startupDeadline = GetTickCount64() + 10000;
    auto pipe = openServer(name, elevated.get(), startupDeadline);
    if (!pipe) return reject(GetLastError());
    ULONG actualServer = 0; Peer server;
    const auto launched = queryProcessIdentity(elevated.get(), GenerationKind::creationTime);
    if (!GetNamedPipeServerProcessId(pipe.get(), &actualServer) || actualServer != GetProcessId(elevated.get())
        || !verifyPeer(actualServer, L"nks-elevation-broker.exe", server) || !sameFile(server, self)
        || launched.state != SampleState::valid || launched.identity != server.key || WaitForSingleObject(elevated.get(), 0) != WAIT_TIMEOUT
        || !sameLogon(server.token, gui.token) || !server.token.elevated || server.token.integrity < SECURITY_MANDATORY_HIGH_RID) return reject(trustRejected);
    Challenge challenge{};
    if (!pipeIo(pipe.get(), challenge, false, elevated.get(), startupDeadline) || read32(challenge, 0) != envelopeMagic
        || read(challenge, 4, 2) != 1 || read(challenge, 6, 2) != challengeBytes || !keyMatches(challenge, 8, server.key)
        || (!read64(challenge, 40) && !read64(challenge, 48)) || read64(challenge, 56) <= GetTickCount64()) return reject(trustRejected);
    const auto binding = envelope(header, self, gui, GetTickCount64() + startupMs, challenge);
    if (!pipeWrite(pipe.get(), binding, elevated.get(), startupDeadline) || !pipeWrite(pipe.get(), header, elevated.get(), startupDeadline)
        || !pipeWrite(pipe.get(), payload, elevated.get(), startupDeadline)) return 3;
    Header reply{};
    if (!pipeIo(pipe.get(), reply, false, nullptr, GetTickCount64() + promptMs + 3000)) return 3;
    bool accepted = false; unsigned frames = 0; const auto operationDeadline = GetTickCount64() + operationMs;
    for (;;) {
        if (read32(reply, 0) != magic || read(reply, 4, 2) != version || read(reply, 6, 2) != read(header, 6, 2)
            || !std::equal(reply.begin() + 16, reply.begin() + 40, header.begin() + 16)
            || read64(reply, 48) || read64(reply, 56)) return 3;
        const auto kind = static_cast<FrameKind>(read32(reply, 40)); const auto length = read32(reply, 8);
        const bool cpuSets = read(header, 6, 2) == static_cast<unsigned>(Command::queryCpuSets) || read(header, 6, 2) == static_cast<unsigned>(Command::cpuSets);
        const auto maximum = cpuSets ? maximumCpuSetsResponse : treePrefixBytes + maximumTreeTargets * (treeNodeBytes + resultBytes);
        if (++frames > 6 || length > maximum) return 3;
        if (kind == FrameKind::accepted) {
            if (accepted || frames != 1 || length || read32(reply, 12) != targets.size() || read32(reply, 44)) return 3;
            accepted = true;
        } else if ((!accepted && kind != FrameKind::rejected) || (accepted && kind != FrameKind::results
            && kind != FrameKind::treePlan && kind != FrameKind::treeResults && kind != FrameKind::cpuSets && kind != FrameKind::failed)) return 3;
        if (kind == FrameKind::cpuSets && (!cpuSets || read32(reply, 12) != 1 || read32(reply, 44)
            || length < resultBytes + cpuSetsPrefixBytes)) return 3;
        if (cpuSets && accepted && kind != FrameKind::accepted && kind != FrameKind::cpuSets && kind != FrameKind::failed) return 3;
        std::vector<std::byte> bytes(length);
        if (!pipeIo(pipe.get(), bytes, false, nullptr, operationDeadline) || !stdioWrite(output, reply) || !stdioWrite(output, bytes)) return 3;
        if (kind != FrameKind::accepted && kind != FrameKind::treePlan) {
            const bool success = kind == FrameKind::results || kind == FrameKind::treeResults || kind == FrameKind::cpuSets;
            // The client accepts results only after a clean broker exit, never a truncated pipe.
            if (WaitForSingleObject(elevated.get(), 1000) != WAIT_OBJECT_0) return 3;
            DWORD exit = 0; if (!GetExitCodeProcess(elevated.get(), &exit) || (success && exit != 0)) return 3;
            return success ? 0 : accepted ? 3 : 2;
        }
        if (!pipeIo(pipe.get(), reply, false, nullptr, operationDeadline)) return 3;
    }
}
}
