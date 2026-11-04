#include <phnt_windows.h>
#include <phnt.h>
#include <sddl.h>
#include <aclapi.h>
#include "collectors/process_actions_wire.hpp"
#include "collectors/process_details.hpp"

#include <algorithm>
#include <array>
#include <cstdio>
#include <filesystem>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>

namespace {
using namespace nks;
using namespace nks::actions;

unsigned checks = 0;
void require(bool value, const std::string& message)
{
    if (!value) throw std::runtime_error(message);
    ++checks;
}

std::filesystem::path selfPath()
{
    std::array<wchar_t, 32768> module{};
    if (!GetModuleFileNameW(nullptr, module.data(), static_cast<DWORD>(module.size()))) throw std::runtime_error("Test path unavailable");
    return module.data();
}

bool writeText(HANDLE pipe, const std::string& text)
{
    DWORD written = 0;
    return WriteFile(pipe, text.data(), static_cast<DWORD>(text.size()), &written, nullptr) && written == text.size();
}

std::string readLine(HANDLE pipe)
{
    std::string line;
    for (char character{}; ;) {
        DWORD count = 0;
        if (!ReadFile(pipe, &character, 1, &count, nullptr) || !count) throw std::runtime_error("Pipe closed before a full line");
        if (character == '\n') return line;
        line += character;
    }
}

// Child side: holds known handles and answers whether a value is still open in its own table.
int child(bool restrictAccess, const wchar_t* file)
{
    const HANDLE input = GetStdHandle(STD_INPUT_HANDLE), output = GetStdHandle(STD_OUTPUT_HANDLE);
    if (restrictAccess) {
        PSECURITY_DESCRIPTOR descriptor = nullptr;
        // Only SYNCHRONIZE and limited query remain; PROCESS_DUP_HANDLE is denied to everyone.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;;0x101000;;;WD)", SDDL_REVISION_1, &descriptor, nullptr)) return 3;
        BOOL present = FALSE, defaulted = FALSE; PACL dacl = nullptr;
        GetSecurityDescriptorDacl(descriptor, &present, &dacl, &defaulted);
        const auto status = SetSecurityInfo(GetCurrentProcess(), SE_KERNEL_OBJECT, DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
            nullptr, nullptr, dacl, nullptr);
        LocalFree(descriptor);
        if (status != ERROR_SUCCESS) return 3;
    }
    const auto name = L"Local\\nks-handle-close-" + std::to_wstring(GetCurrentProcessId());
    const HANDLE plain = CreateEventW(nullptr, TRUE, FALSE, nullptr), named = CreateEventW(nullptr, TRUE, FALSE, name.c_str());
    const HANDLE guarded = CreateEventW(nullptr, TRUE, FALSE, nullptr), spare = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    const HANDLE exclusive = file ? CreateFileW(file, GENERIC_READ | GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_TEMPORARY, nullptr) : nullptr;
    if (!plain || !named || !guarded || !spare || exclusive == INVALID_HANDLE_VALUE
        || !SetHandleInformation(guarded, HANDLE_FLAG_PROTECT_FROM_CLOSE, HANDLE_FLAG_PROTECT_FROM_CLOSE)) return 4;
    char ready[160]{};
    std::snprintf(ready, sizeof(ready), "%llx %llx %llx %llx %llx\n", reinterpret_cast<unsigned long long>(plain),
        reinterpret_cast<unsigned long long>(named), reinterpret_cast<unsigned long long>(guarded),
        reinterpret_cast<unsigned long long>(spare), reinterpret_cast<unsigned long long>(exclusive));
    if (!writeText(output, ready)) return 5;
    for (;;) {
        std::string line;
        try { line = readLine(input); } catch (const std::runtime_error&) { return 0; }
        if (line.rfind("check ", 0) != 0) return 0;
        const auto value = reinterpret_cast<HANDLE>(static_cast<std::uintptr_t>(std::stoull(line.substr(6), nullptr, 16)));
        DWORD flags = 0;
        if (!writeText(output, GetHandleInformation(value, &flags) ? "open\n" : "closed\n")) return 5;
    }
}

struct Child {
    HANDLE input{}, output{}, job{}, process{};
    std::vector<std::uint64_t> handles;
    Target target;

    explicit Child(const std::wstring& arguments)
    {
        SECURITY_ATTRIBUTES security{sizeof(security), nullptr, TRUE};
        HANDLE childInput{}, childOutput{};
        if (!CreatePipe(&childInput, &input, &security, 0) || !CreatePipe(&output, &childOutput, &security, 0)) throw std::runtime_error("Pipes failed");
        SetHandleInformation(input, HANDLE_FLAG_INHERIT, 0); SetHandleInformation(output, HANDLE_FLAG_INHERIT, 0);
        job = CreateJobObjectW(nullptr, nullptr);
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!job || !NT_SUCCESS(NtSetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, sizeof(limits)))) throw std::runtime_error("Job failed");
        auto command = L"\"" + selfPath().wstring() + L"\" " + arguments;
        STARTUPINFOW startup{}; startup.cb = sizeof(startup); startup.dwFlags = STARTF_USESTDHANDLES;
        startup.hStdInput = childInput; startup.hStdOutput = childOutput; startup.hStdError = GetStdHandle(STD_ERROR_HANDLE);
        PROCESS_INFORMATION created{};
        const bool started = CreateProcessW(nullptr, command.data(), nullptr, nullptr, TRUE, CREATE_SUSPENDED | CREATE_NO_WINDOW,
            nullptr, nullptr, &startup, &created) != FALSE;
        CloseHandle(childInput); CloseHandle(childOutput);
        if (!started) throw std::runtime_error("Child start failed");
        process = created.hProcess;
        if (!AssignProcessToJobObject(job, process)) { TerminateProcess(process, 1); CloseHandle(created.hThread); throw std::runtime_error("Job assignment failed"); }
        ResumeThread(created.hThread); CloseHandle(created.hThread);
        const auto line = readLine(output);
        for (std::size_t at = 0; at < line.size();) {
            std::size_t used = 0; handles.push_back(std::stoull(line.substr(at), &used, 16)); at += used;
            while (at < line.size() && line[at] == ' ') ++at;
        }
        if (handles.size() != 5) throw std::runtime_error("Child ready line is malformed");
        const auto identity = queryProcessIdentity(process, GenerationKind::creationTime);
        FILETIME start{}, end{}, kernel{}, user{};
        if (identity.state != SampleState::valid || !GetProcessTimes(process, &start, &end, &kernel, &user)) throw std::runtime_error("Child identity unavailable");
        target.key = identity.identity;
        target.created100ns = (static_cast<std::uint64_t>(start.dwHighDateTime) << 32) | start.dwLowDateTime;
    }
    ~Child()
    {
        if (input) CloseHandle(input);
        if (process) { WaitForSingleObject(process, 2000); CloseHandle(process); }
        if (output) CloseHandle(output);
        if (job) CloseHandle(job);
    }
    bool open(std::uint64_t value)
    {
        char text[40]{}; std::snprintf(text, sizeof(text), "check %llx\n", static_cast<unsigned long long>(value));
        if (!writeText(input, text)) throw std::runtime_error("Child check write failed");
        const auto answer = readLine(output);
        if (answer != "open" && answer != "closed") throw std::runtime_error("Child check answer is malformed");
        return answer == "open";
    }
};

HandleSpec specFor(HANDLE process, std::uint64_t value, bool withName)
{
    ProcessDetailsResult table;
    readProcessHandles(process, table);
    if (table.state != details::ResultState::ok) throw std::runtime_error("Child handle table unavailable");
    HandleSpec spec; spec.value = value;
    bool found = false;
    for (const auto& row : table.handles) if (row.value == value) { spec.grantedAccess = row.grantedAccess; spec.typeIndex = row.typeIndex; found = true; }
    HANDLE raw = nullptr;
    if (!found || !DuplicateHandle(process, reinterpret_cast<HANDLE>(static_cast<std::uintptr_t>(value)), GetCurrentProcess(), &raw, 0, FALSE, 0))
        throw std::runtime_error("Child handle cannot be observed");
    Handle copy(raw);
    if (!NT_SUCCESS(queryObjectText(raw, false, spec.type)) || (withName && !NT_SUCCESS(queryObjectText(raw, true, spec.name))))
        throw std::runtime_error("Child object text unavailable");
    if (withName) spec.flags = verifyHandleName;
    return spec;
}

Result run(const Context& context, const Target& target, const HandleSpec& spec)
{
    Result result; result.target = target;
    closeHandle(context, spec, result);
    return result;
}

void refused(const Result& result, Reason reason, const char* name)
{
    require(result.outcome == Outcome::refused && result.reason == reason && !(result.flags & (mayHaveApplied | effectConfirmed)),
        std::string(name) + ": expected refusal " + std::to_string(static_cast<unsigned>(reason)) + ", got outcome "
            + std::to_string(static_cast<unsigned>(result.outcome)) + " reason " + std::to_string(static_cast<unsigned>(result.reason)));
}

void closed(const Result& result, const char* name)
{
    constexpr auto expected = identityVerified | aliveBefore | effectConfirmed | mayHaveApplied | aliveAfter;
    require(result.outcome == Outcome::applied && (result.flags & expected) == expected && !result.error,
        std::string(name) + ": close not confirmed, outcome " + std::to_string(static_cast<unsigned>(result.outcome))
            + " reason " + std::to_string(static_cast<unsigned>(result.reason)) + " error " + std::to_string(result.error));
}

void wireChecks()
{
    wire::Header header{};
    write(header, 0, magic, 4); write(header, 4, version, 2); write(header, 6, static_cast<unsigned>(Command::closeHandle), 2);
    write(header, 12, 1, 4); write(header, 16, 7, 8); write(header, 24, 9, 8); write(header, 40, 0x44, 8);
    std::vector<std::byte> payload(targetBytes + handleSpecBytes + 2 * 5);
    write(payload, targetBytes + 8, 5, 4);
    for (std::size_t i = 0; i < 5; ++i) write(payload, targetBytes + handleSpecBytes + i * 2, L"Event"[i], 2);
    write(header, 8, payload.size(), 4);
    HandleSpec spec;
    require(wire::headerValid(header) && wire::closeHandleRequest(header, payload, spec) && spec.type == L"Event" && spec.value == 0x44,
        "A well-formed close request was rejected");
    auto changed = header; write(changed, 40, 0x46, 8);
    require(!wire::headerValid(changed), "An unaligned handle value was accepted");
    changed = header; write(changed, 40, 0, 8);
    require(!wire::headerValid(changed), "A zero handle value was accepted");
    changed = header; write(changed, 12, 2, 4);
    require(!wire::headerValid(changed), "A close request with two targets was accepted");
    changed = header; write(changed, 48, 2, 4);
    require(!wire::headerValid(changed), "An unknown close flag was accepted");
    auto bad = payload; write(bad, targetBytes + 12, 1, 4);
    require(!wire::closeHandleRequest(header, bad, spec), "A name without its verification flag or bytes was accepted");
    bad = payload; write(bad, targetBytes + handleSpecBytes + 2, 0, 2);
    require(!wire::closeHandleRequest(header, bad, spec), "A NUL inside the type name was accepted");
    bad = payload; write(bad, targetBytes + 8, 0, 4); bad.resize(targetBytes + handleSpecBytes);
    require(!wire::closeHandleRequest(header, bad, spec), "An empty type name was accepted");
}

// Sends one NKA1 close request through the real asInvoker worker and returns the result row.
std::array<std::byte, resultBytes> workerClose(const Target& target, const HandleSpec& spec, std::uint32_t& frame)
{
    SECURITY_ATTRIBUTES security{sizeof(security), nullptr, TRUE};
    HANDLE childInput{}, input{}, output{}, childOutput{};
    if (!CreatePipe(&childInput, &input, &security, 0) || !CreatePipe(&output, &childOutput, &security, 0)) throw std::runtime_error("Worker pipes failed");
    SetHandleInformation(input, HANDLE_FLAG_INHERIT, 0); SetHandleInformation(output, HANDLE_FLAG_INHERIT, 0);
    auto command = L"\"" + (selfPath().parent_path() / L"nks-actions-worker.exe").wstring() + L"\" --stdio";
    STARTUPINFOW startup{}; startup.cb = sizeof(startup); startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = childInput; startup.hStdOutput = childOutput; startup.hStdError = childOutput;
    PROCESS_INFORMATION created{};
    const bool started = CreateProcessW(nullptr, command.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &created) != FALSE;
    CloseHandle(childInput); CloseHandle(childOutput);
    Handle in(input), out(output), worker(started ? created.hProcess : nullptr);
    if (!started) throw std::runtime_error("Actions worker start failed");
    CloseHandle(created.hThread);
    const auto chars = spec.type.size() + spec.name.size();
    std::vector<std::byte> request(headerBytes + targetBytes + handleSpecBytes + chars * 2);
    std::span<std::byte> bytes(request);
    write(bytes, 0, magic, 4); write(bytes, 4, version, 2); write(bytes, 6, static_cast<unsigned>(Command::closeHandle), 2);
    write(bytes, 8, request.size() - headerBytes, 4); write(bytes, 12, 1, 4); write(bytes, 16, 0x1234, 8); write(bytes, 24, 0x5678, 8);
    write(bytes, 40, spec.value, 8); write(bytes, 48, spec.flags, 4);
    wire::targetBytesTo(target, bytes.subspan(headerBytes, targetBytes));
    const auto block = headerBytes + targetBytes;
    write(bytes, block, spec.grantedAccess, 4); write(bytes, block + 4, spec.typeIndex, 4);
    write(bytes, block + 8, spec.type.size(), 4); write(bytes, block + 12, spec.name.size(), 4);
    const auto text = spec.type + spec.name;
    for (std::size_t i = 0; i < text.size(); ++i) write(bytes, block + handleSpecBytes + i * 2, static_cast<std::uint16_t>(text[i]), 2);
    DWORD written = 0;
    if (!WriteFile(in.get(), request.data(), static_cast<DWORD>(request.size()), &written, nullptr) || written != request.size()) throw std::runtime_error("Worker request write failed");
    in.reset();
    const auto readExact = [&](std::span<std::byte> rest) {
        while (!rest.empty()) {
            DWORD count = 0;
            if (!ReadFile(out.get(), rest.data(), static_cast<DWORD>(rest.size()), &count, nullptr) || !count) throw std::runtime_error("Worker response truncated");
            rest = rest.subspan(count);
        }
    };
    wire::Header reply{};
    readExact(reply);
    frame = read32(reply, 40);
    std::array<std::byte, resultBytes> row{};
    if (frame == static_cast<std::uint32_t>(FrameKind::accepted)) {
        readExact(reply); frame = read32(reply, 40);
        if (frame != static_cast<std::uint32_t>(FrameKind::results) || read32(reply, 8) != resultBytes) throw std::runtime_error("Worker returned no result row");
        readExact(row);
    }
    if (WaitForSingleObject(worker.get(), 5000) != WAIT_OBJECT_0) { TerminateProcess(worker.get(), 1); throw std::runtime_error("Worker did not exit"); }
    return row;
}

int parent()
{
    wireChecks();
    const auto context = actionContext();
    require(context.ready, "The test runner parent is not a verifiable same-user process; run under ctest or a shell");
    const auto temp = std::filesystem::temp_directory_path() / (L"nks-handle-close-" + std::to_wstring(GetCurrentProcessId()) + L".tmp");
    Child first(L"--child \"" + temp.wstring() + L"\"");
    const auto plain = first.handles[0], named = first.handles[1], guarded = first.handles[2], spare = first.handles[3], file = first.handles[4];

    const auto namedSpec = specFor(first.process, named, true);
    require(namedSpec.type == L"Event" && namedSpec.name.find(L"nks-handle-close-") != std::wstring::npos, "Named event observation is wrong");

    // Identity: the live PID with another generation, a different raw time and another process's full key are all refused.
    auto target = first.target; ++target.key.generation;
    auto result = run(context, target, namedSpec);
    require(result.outcome == Outcome::identityChanged && !(result.flags & mayHaveApplied), "A wrong generation was not refused");
    target = first.target; ++target.created100ns;
    result = run(context, target, namedSpec);
    require(result.outcome == Outcome::identityChanged && result.reason == Reason::rawTimeMismatch && !(result.flags & mayHaveApplied),
        "A changed raw creation time was not refused");
    {
        Child second(L"--child");
        target = second.target; target.key.pid = first.target.key.pid;
        result = run(context, target, namedSpec);
        require(result.outcome == Outcome::identityChanged && !(result.flags & mayHaveApplied), "A reused PID with another identity was not refused");
        CloseHandle(second.input); second.input = nullptr;
        require(WaitForSingleObject(second.process, 5000) == WAIT_OBJECT_0, "Second child did not exit");
        result = run(context, second.target, namedSpec);
        require((result.outcome == Outcome::alreadyExited || result.outcome == Outcome::refused) && !(result.flags & mayHaveApplied),
            "An ended process was not refused");
    }
    require(first.open(named), "A refused identity request closed the handle");

    // Handle object: type, access, type index, name and a different object under the value are refused.
    auto spec = namedSpec; spec.type = L"Mutant";
    refused(run(context, first.target, spec), Reason::handleChanged, "type");
    spec = namedSpec; spec.grantedAccess ^= 1;
    refused(run(context, first.target, spec), Reason::handleChanged, "access");
    spec = namedSpec; ++spec.typeIndex;
    refused(run(context, first.target, spec), Reason::handleChanged, "type index");
    spec = namedSpec; spec.name += L"-other";
    refused(run(context, first.target, spec), Reason::handleChanged, "name");
    spec = specFor(first.process, plain, true); spec.value = named;
    refused(run(context, first.target, spec), Reason::handleChanged, "unnamed object under named value");
    require(first.open(named), "A refused object request closed the handle");
    {
        ProcessDetailsResult table; readProcessHandles(first.process, table);
        std::uint64_t unused = 4;
        while (std::any_of(table.handles.begin(), table.handles.end(), [&](const auto& entry) { return entry.value == unused; })) unused += 4;
        spec = namedSpec; spec.value = unused;
        refused(run(context, first.target, spec), Reason::handleMissing, "missing value");
    }
    refused(run(context, first.target, specFor(first.process, guarded, false)), Reason::handleProtected, "protect from close");
    require(first.open(guarded), "A protected handle was closed");
    {
        Target own;
        const auto identity = queryProcessIdentity(GetCurrentProcess(), GenerationKind::creationTime);
        own.key = identity.identity; own.created100ns = identity.identity.generation;
        refused(run(context, own, namedSpec), Reason::ownOrParent, "own process");
    }

    // Real closes, observed by the child and, for the exclusive file, by an independent reopen.
    closed(run(context, first.target, specFor(first.process, plain, true)), "event");
    require(!first.open(plain), "The child still holds the closed event");
    const auto fileSpec = specFor(first.process, file, true);
    require(fileSpec.type == L"File", "Exclusive file observation is wrong");
    closed(run(context, first.target, fileSpec), "file");
    require(!first.open(file), "The child still holds the closed file");
    Handle reopened(CreateFileW(temp.c_str(), GENERIC_READ, 0, nullptr, OPEN_EXISTING, 0, nullptr));
    require(reopened.get() != INVALID_HANDLE_VALUE, "The exclusive file is still open somewhere after the close");
    reopened.reset(); DeleteFileW(temp.c_str());
    refused(run(context, first.target, fileSpec), Reason::handleMissing, "second close of the same value");

    // The same command through the asInvoker worker and its NKA1 validation.
    std::uint32_t frame = 0;
    const auto row = workerClose(first.target, specFor(first.process, spare, true), frame);
    require(frame == static_cast<std::uint32_t>(FrameKind::results) && read32(row, 48) == static_cast<std::uint32_t>(Outcome::applied)
        && (read32(row, 64) & effectConfirmed) && read32(row, 16) == first.target.key.pid, "Worker did not confirm the close");
    require(!first.open(spare), "The child still holds the handle closed by the worker");
    spec = namedSpec; spec.flags = 0;
    workerClose(first.target, spec, frame);
    require(frame == static_cast<std::uint32_t>(FrameKind::rejected) && first.open(named), "Worker accepted an unflagged object name");

    // Access denied on an own child whose DACL withholds PROCESS_DUP_HANDLE.
    Child locked(L"--restricted-child");
    if (Handle probe(OpenProcess(PROCESS_DUP_HANDLE, FALSE, locked.target.key.pid)); probe) {
        std::cout << "handle-close: access-denied case skipped, this runner bypasses the target DACL\n";
    } else {
        spec = HandleSpec{}; spec.value = locked.handles[0]; spec.type = L"Event";
        refused(run(context, locked.target, spec), Reason::accessDenied, "access denied");
        require(locked.open(locked.handles[0]), "A denied request closed the handle");
    }
    return 0;
}
}

int wmain(int argc, wchar_t** argv)
{
    try {
        if (argc >= 2 && std::wstring_view(argv[1]) == L"--child") return child(false, argc >= 3 ? argv[2] : nullptr);
        if (argc >= 2 && std::wstring_view(argv[1]) == L"--restricted-child") return child(true, nullptr);
        const auto code = parent();
        std::cout << "handle-close: " << checks << " checks passed\n";
        return code;
    } catch (const std::exception& error) {
        std::cerr << "handle-close: " << error.what() << '\n';
        return 1;
    }
}
