#include "execute.hpp"
#include <algorithm>
#include <atomic>
#include <thread>
#include <string_view>

namespace {
using namespace nks::update;
using nks::broker::Handle;
bool io(HANDLE pipe, std::span<std::byte> bytes, bool writing)
{
    while (!bytes.empty()) {
        DWORD count = 0;
        const BOOL done = writing ? WriteFile(pipe, bytes.data(), static_cast<DWORD>(bytes.size()), &count, nullptr)
            : ReadFile(pipe, bytes.data(), static_cast<DWORD>(bytes.size()), &count, nullptr);
        if (!done || !count || count > bytes.size()) return false;
        bytes = bytes.subspan(count);
    }
    return true;
}
bool send(HANDLE pipe, const Header& request, Frame frame, std::span<const std::byte> payload)
{
    Header response = request; write(response, 8, payload.size(), 4); write(response, 12, static_cast<unsigned>(frame), 4);
    return io(pipe, response, true) && io(pipe, {const_cast<std::byte*>(payload.data()), payload.size()}, true);
}
bool eof(HANDLE input)
{
    BYTE extra{}; DWORD count = 0; const BOOL done = ReadFile(input, &extra, 1, &count, nullptr);
    return !count && (done || GetLastError() == ERROR_BROKEN_PIPE || GetLastError() == ERROR_HANDLE_EOF);
}
Hash selected(const Header& header) { Hash hash{}; std::copy_n(header.begin() + 88, 32, hash.begin()); return hash; }
std::wstring explanation(const char* message)
{
    std::wstring result;
    for (; *message; ++message) result += static_cast<unsigned char>(*message);
    return result;
}
int bootstrap(const std::wstring& source)
{
    nks::broker::Peer self;
    if (!nks::broker::verifyPeer(GetCurrentProcessId(), L"nks-update-worker.exe", self)) return 2;
    auto worker = openImage(self.path, Role::worker);
    auto installed = openImage(parentPath(self.path) + L"\\NksTaskMgr.WinUI.exe", Role::application);
    auto installer = openImage(source, Role::installer);
    if (!sameVersion(worker, installed) || !sameVersion(installer, installed) || !equalId(worker.lease.fileId, self.fileId)) return 2;
    Cache cache; static_cast<void>(cache.store(installer)); return 0;
}
int serve(std::atomic<ULONGLONG>& deadline)
{
    const HANDLE input = GetStdHandle(STD_INPUT_HANDLE), output = GetStdHandle(STD_OUTPUT_HANDLE);
    if (GetFileType(input) != FILE_TYPE_PIPE || GetFileType(output) != FILE_TYPE_PIPE || input == output) return 2;
    Header request{}, reply{}; bool received = false, accepted = false;
    std::optional<Plan> staged;
    std::unique_ptr<Cache> cache;
    try {
        if (!io(input, request, false)) return 2;
        reply = request; received = true;
        const auto command = static_cast<Command>(read(request, 6, 2)); const auto length = static_cast<std::size_t>(read(request, 8, 4));
        if (command < Command::list || command > Command::rollback || !validHeader(request, command, length)) throw Error(Status::invalidRequest, "Invalid update request");
        std::vector<std::byte> payload(length);
        if (!io(input, payload, false)) throw Error(Status::invalidRequest, "Truncated update request");
        const bool applying = command == Command::apply || command == Command::rollback;
        if ((!applying && !eof(input)) || ((command == Command::list || command == Command::cache) && !empty(selected(request)))
            || ((command == Command::list || command == Command::plan) && !payload.empty()) || (applying && (payload.size() != 16 || empty(selected(request)))))
            throw Error(Status::invalidRequest, "Unexpected update request payload");
        auto context = authenticate(request, applying); cache = std::make_unique<Cache>();
        if (command == Command::list) {
            auto packages = cache->packages(); const auto entries = journalEntries(*cache); const auto previous = previousVersion(entries, context.installed);
            const auto body = result(Status::ok, 0, observedState(entries), {}, &packages, nullptr, 0, context.installed.version, previous);
            return send(output, request, Frame::result, body) ? 0 : 3;
        }
        if (command == Command::cache) {
            auto source = cacheSource(context, payload); std::vector<Image> stored; stored.push_back(cache->store(source));
            const auto body = result(Status::ok, 0, State::cached, {}, &stored, nullptr, 0, context.installed.version);
            return send(output, request, Frame::result, body) ? 0 : 3;
        }
        if (command == Command::plan) {
            const auto plan = createPlan(*cache, context, selected(request)); const auto body = result(Status::ok, 0, State::planned, {}, nullptr, &plan);
            return send(output, request, Frame::result, body) ? 0 : 3;
        }
        staged = loadPlan(*cache, context, request, payload);
        auto package = openImage(staged->packagePath(), Role::installer);
        if (!matches(staged->package, package) || package.version != staged->selectedVersion()) throw Error(Status::rejected, "Selected installer no longer matches the plan");
        const auto commitDeadline = std::min<ULONGLONG>(GetTickCount64() + 30000, read(request, 40, 8));
        deadline.store(commitDeadline + 1000);
        if (!send(output, request, Frame::ready, result(Status::ok, 0, State::planned, {}, nullptr, &*staged, commitDeadline))) return 3;
        Header commit{};
        if (!io(input, commit, false)) throw Error(Status::rejected, "Update was not committed");
        Header expected = request; write(expected, 6, static_cast<unsigned>(Command::commit), 2); write(expected, 8, 0, 4);
        if (commit != expected || GetTickCount64() >= commitDeadline || !validHeader(commit, Command::commit, 0) || !eof(input)
            || !nks::broker::peerAlive(context.gui) || !matches(staged->installed, context.installed)) throw Error(Status::rejected, "Commit binding expired or changed");
        reply = commit;
        journal(*cache, *staged, State::committed, 1);
        if (!send(output, commit, Frame::accepted, result(Status::ok, 0, State::committed))) {
            journal(*cache, *staged, State::rejected, 4, ERROR_BROKEN_PIPE); return 3;
        }
        accepted = true;
        CloseHandle(input); CloseHandle(output); SetStdHandle(STD_INPUT_HANDLE, nullptr); SetStdHandle(STD_OUTPUT_HANDLE, nullptr);
        deadline.store(GetTickCount64() + 32ull * 60 * 1000);
        install(*cache, context, *staged, package); return 0;
    } catch (const Error& error) {
        if (accepted) {
            if (cache && staged) try { journal(*cache, *staged, State::unknown, 4, error.windows); } catch (...) { }
            return 3;
        }
        if (cache && staged) try { journal(*cache, *staged, State::rejected, 4, error.windows); } catch (...) { }
        if (received) send(output, reply, Frame::rejected, result(error.status, error.windows, State::rejected, explanation(error.what())));
        return error.status == Status::io || error.status == Status::unknown ? 3 : 2;
    } catch (...) {
        if (received && !accepted) send(output, reply, Frame::rejected, result(Status::unknown, ERROR_GEN_FAILURE, State::unknown, L"Update operation did not complete."));
        return 3;
    }
}
}

int wmain(int argc, wchar_t** argv)
{
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    if (!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32)) return 2;
    auto deadline = std::make_shared<std::atomic<ULONGLONG>>(GetTickCount64() + 120000);
    std::thread([deadline] {
        for (;;) { Sleep(250); if (GetTickCount64() >= deadline->load()) TerminateProcess(GetCurrentProcess(), ERROR_TIMEOUT); }
    }).detach();
    if (argc == 2 && std::wstring_view(argv[1]) == L"--stdio") return serve(*deadline);
    if (argc == 3 && std::wstring_view(argv[1]) == L"--cache-installer") {
        try { return bootstrap(argv[2]); }
        catch (const Error& error) { return error.status == Status::io || error.status == Status::unknown || error.status == Status::cacheFull ? 3 : 2; }
        catch (...) { return 3; }
    }
    return 2;
}
