#include "execute.hpp"
#include <aclapi.h>
#include <bcrypt.h>
#include <algorithm>
#include <thread>

namespace nks::update {
namespace {
Hash headerHash(const Header& header) { Hash hash{}; std::copy_n(header.begin() + 88, 32, hash.begin()); return hash; }
void validateAppDirectory(const Context& context)
{
    if (context.installDir.find(L'"') != std::wstring::npos) throw Error(Status::rejected, "Installation directory contains unsupported characters");
    broker::Handle folder(CreateFileW(context.installDir.c_str(), READ_CONTROL | FILE_READ_ATTRIBUTES, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    PSECURITY_DESCRIPTOR descriptor = nullptr; PSID owner = nullptr;
    const DWORD status = folder && folder.get() != INVALID_HANDLE_VALUE ? GetSecurityInfo(folder.get(), SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION,
        &owner, nullptr, nullptr, nullptr, &descriptor) : ERROR_ACCESS_DENIED;
    const bool valid = !status && owner && EqualSid(owner, const_cast<std::byte*>(context.gui.token.user.data()));
    if (descriptor) LocalFree(descriptor);
    if (!valid) throw Error(Status::rejected, "Installation directory is not owned by the current user", status);
}
bool equalPath(const std::wstring& left, const std::wstring& right) { return _wcsicmp(left.c_str(), right.c_str()) == 0; }
bool exists(const std::wstring& path) { return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES; }
}

bool validHeader(const Header& header, Command command, std::size_t payload)
{
    const auto now = GetTickCount64(), expires = read(header, 40, 8);
    return read(header, 0, 4) == magic && read(header, 4, 2) == 1 && read(header, 6, 2) == static_cast<unsigned>(command)
        && read(header, 8, 4) == payload && payload <= maxPayload && read(header, 12, 4) == 0
        && read(header, 16, 8) && (read(header, 24, 8) || read(header, 32, 8)) && !read(header, 120, 8)
        && expires > now && expires - now <= 120000;
}
Context authenticate(const Header& header, bool staged)
{
    if (!broker::trustConfigured()) throw Error(Status::trustMissing, "Update publisher trust is not configured", broker::trustNotConfigured);
    Context context;
    if (!broker::verifyPeer(GetCurrentProcessId(), L"nks-update-worker.exe", context.self)
        || !broker::verifyPeer(broker::parentPid(GetCurrentProcess()), L"NksTaskMgr.WinUI.exe", context.gui)
        || !broker::sameLogon(context.self.token, context.gui.token) || context.self.token.elevated || context.gui.token.elevated
        || context.gui.key.boot != context.self.key.boot || context.gui.key.generation > context.self.key.generation)
        throw Error(Status::rejected, "Actual updater client is not a verified ordinary GUI", GetLastError());
    const auto expected = readKey(header, 48);
    if (expected != context.gui.key || read(header, 80, 8) != context.gui.key.generation) throw Error(Status::rejected, "GUI process identity mismatch");
    context.worker = openImage(context.self.path, Role::worker); context.installed = openImage(context.gui.path, Role::application);
    if (!equalId(context.worker.lease.fileId, context.self.fileId) || !equalId(context.installed.lease.fileId, context.gui.fileId)
        || !sameVersion(context.worker, context.installed)) throw Error(Status::rejected, "Worker and GUI release binding differ");
    context.installDir = parentPath(context.gui.path); validateAppDirectory(context);
    if (!staged && !equalPath(parentPath(context.self.path), context.installDir)) throw Error(Status::rejected, "Read-only updater is not in the running application directory");
    return context;
}

Image cacheSource(Context& context, std::span<const std::byte> payload)
{
    if (payload.size() < 80 || read(payload, 76, 4)) throw Error(Status::invalidRequest, "Invalid cache source layout");
    const auto sourceHandle = read(payload, 0, 8), size = read(payload, 8, 8); const auto id = readId(payload, 16);
    Hash expected{}; std::copy_n(payload.begin() + 40, 32, expected.begin());
    std::size_t at = 80; const auto path = text(payload, at, static_cast<std::size_t>(read(payload, 72, 4)));
    if (at != payload.size() || path.empty() || !sourceHandle || sourceHandle == UINT64_MAX || !size || size > maxPackageSize || empty(expected))
        throw Error(Status::invalidRequest, "Invalid cache source proof");
    broker::Handle client(OpenProcess(PROCESS_DUP_HANDLE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, context.gui.key.pid));
    if (!client) throw Error(Status::rejected, "Cannot hold source owner", GetLastError());
    const auto actual = queryProcessIdentity(client.get(), context.gui.key.generationKind);
    if (actual.state != SampleState::valid || actual.identity != context.gui.key || WaitForSingleObject(client.get(), 0) != WAIT_TIMEOUT)
        throw Error(Status::rejected, "Source owner changed");
    HANDLE copied = nullptr;
    if (!DuplicateHandle(client.get(), reinterpret_cast<HANDLE>(static_cast<std::uintptr_t>(sourceHandle)), GetCurrentProcess(), &copied,
        GENERIC_READ | FILE_EXECUTE | SYNCHRONIZE, FALSE, 0)) throw Error(Status::rejected, "Cannot duplicate source file", GetLastError());
    auto source = inspectImage(broker::Handle(copied), Role::installer);
    if (source.hash != expected || source.size != size || !equalId(source.lease.fileId, id) || !equalPath(source.lease.path, path))
        throw Error(Status::rejected, "Source file does not match its held proof");
    return source;
}

Plan createPlan(Cache& cache, Context& context, const Hash& requested)
{
    auto packages = cache.packages(); const auto entries = journalEntries(cache);
    for (const auto& entry : entries) if (entry.state == State::committed || entry.state == State::waitingGui
        || entry.state == State::installing || entry.state == State::unknown)
        throw Error(Status::conflict, "A previous update has an unknown outcome; installation must be inspected before another plan");
    const auto baseline = std::count_if(packages.begin(), packages.end(), [&](const auto& package) { return sameVersion(package, context.installed); });
    if (baseline != 1) throw Error(Status::notFound, "Add the signed setup of the current exact version and revision before preparing an update or rollback");
    const auto previous = previousVersion(entries, context.installed);
    Image* selected = nullptr; const bool rollback = empty(requested);
    for (auto& package : packages) if (rollback ? !previous.empty() && package.version == previous : package.hash == requested) {
        if (selected) throw Error(Status::unknown, "Selected cached release is ambiguous"); selected = &package;
    }
    if (!selected) throw Error(Status::notFound, rollback ? "Previous verified package is unavailable" : "Selected package is not in the private cache");
    if (sameVersion(*selected, context.installed) || (!rollback && selected->numbers < context.installed.numbers))
        throw Error(Status::conflict, "Selected package is not an eligible update");
    if (context.installDir.starts_with(cache.path())) throw Error(Status::rejected, "Installation cannot target the update cache");
    Plan plan;
    if (BCryptGenRandom(nullptr, reinterpret_cast<PUCHAR>(plan.id.data()), static_cast<ULONG>(plan.id.size()), BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0)
        throw Error(Status::io, "Cannot generate update plan identity");
    plan.action = rollback ? Action::rollback : Action::update; plan.created = unixMs(); plan.expires = GetTickCount64() + 600000;
    auto stage = cache.stage(context.worker, plan.id);
    plan.package = proof(*selected); plan.installed = proof(context.installed); plan.stage = proof(stage);
    plan.gui = context.gui.key; plan.guiCreated = context.gui.key.generation;
    plan.strings = {context.installDir, stage.lease.path, selected->version, context.installed.version, selected->lease.path, context.installed.version};
    writeNew(cache.planPath(plan.id), encodePlan(plan)); journal(cache, plan, State::planned, 0); return plan;
}
Plan loadPlan(Cache& cache, Context& context, const Header& request, std::span<const std::byte> payload)
{
    if (payload.size() != 16) throw Error(Status::invalidRequest, "Staged request requires one exact plan identity");
    PlanId id{}; std::copy(payload.begin(), payload.end(), id.begin());
    auto plan = decodePlan(readData(cache.planPath(id)));
    const auto now = GetTickCount64();
    if (plan.id != id || plan.gui != context.gui.key || plan.guiCreated != context.gui.key.generation || plan.package.hash != headerHash(request)
        || !equalPath(plan.stagePath(), cache.stagePath(plan.id)) || !equalPath(plan.packagePath(), cache.packagePath(plan.package.hash))
        || !equalPath(plan.installDir(), context.installDir) || !equalPath(context.self.path, plan.stagePath())
        || plan.currentVersion() != context.installed.version || !matches(plan.installed, context.installed) || !matches(plan.stage, context.worker)
        || plan.expires <= now || plan.expires - now > 600000 || exists(cache.journalPath(plan.id, 1)) || exists(cache.journalPath(plan.id, 4)))
        throw Error(Status::rejected, "Staged plan proof is stale or no longer bound to the actual client");
    const auto command = static_cast<Command>(read(request, 6, 2));
    if ((command == Command::apply) != (plan.action == Action::update)) throw Error(Status::rejected, "Staged action does not match the selected plan");
    if (plan.action == Action::rollback) {
        const auto previous = previousVersion(journalEntries(cache), context.installed);
        if (previous.empty() || previous != plan.selectedVersion()) throw Error(Status::rejected, "Rollback is no longer the verified previous release");
    }
    const auto packages = cache.packages();
    if (plan.strings[5] != context.installed.version || std::count_if(packages.begin(), packages.end(), [&](const auto& package) {
        return sameVersion(package, context.installed);
    }) != 1) throw Error(Status::notFound, "Verified current-release rollback baseline is no longer available");
    return plan;
}

void install(Cache& cache, Context& context, Plan& plan, Image& package)
{
    journal(cache, plan, State::waitingGui, 2);
    if (WaitForSingleObject(context.gui.process.get(), 30000) != WAIT_OBJECT_0) { journal(cache, plan, State::guiAlive, 4, ERROR_TIMEOUT); return; }
    // Image locks are released only after the exact original GUI has exited; directory anchors remain held.
    context.gui.image.reset(); context.installed.lease.image.reset();
    if (package.hash != plan.package.hash || !matches(plan.package, package) || package.version != plan.selectedVersion())
        throw Error(Status::verificationFailed, "Installer proof changed before execution");
    journal(cache, plan, State::installing, 3);
    std::wstring command = L"\"" + package.lease.path + L"\" /CURRENTUSER /NOCLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /NORESTART /DIR=\"" + plan.installDir() + L"\"";
    wchar_t system[32768]{}; const UINT systemSize = GetSystemDirectoryW(system, static_cast<UINT>(std::size(system)));
    if (!systemSize || systemSize >= std::size(system)) throw Error(Status::io, "System directory is unavailable");
    STARTUPINFOW startup{}; startup.cb = sizeof(startup); PROCESS_INFORMATION process{};
    if (!CreateProcessW(package.lease.path.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, system, &startup, &process)) {
        journal(cache, plan, State::installerFailed, 4, GetLastError()); return;
    }
    broker::Handle installer(process.hProcess), thread(process.hThread);
    if (WaitForSingleObject(installer.get(), 30 * 60 * 1000) != WAIT_OBJECT_0) { journal(cache, plan, State::unknown, 4, ERROR_TIMEOUT); return; }
    DWORD exit = 0;
    if (!GetExitCodeProcess(installer.get(), &exit)) { journal(cache, plan, State::unknown, 4, GetLastError()); return; }
    if (exit != 0) { journal(cache, plan, State::installerFailed, 4, 0, exit); return; }
    try {
        auto installed = openImage(plan.installDir() + L"\\NksTaskMgr.WinUI.exe", Role::application);
        if (installed.version != plan.selectedVersion()) { journal(cache, plan, State::verificationFailed, 4, ERROR_REVISION_MISMATCH, exit); return; }
        journal(cache, plan, State::verified, 4, 0, exit);
    } catch (const Error& error) { journal(cache, plan, State::verificationFailed, 4, error.windows, exit); }
}

std::vector<std::byte> result(Status status, DWORD error, State state, const std::wstring& detail,
    const std::vector<Image>* packages, const Plan* plan, std::uint64_t commitDeadline, const std::wstring& current, const std::wstring& previous)
{
    std::vector<std::byte> bytes(resultBytes); write(bytes, 0, static_cast<unsigned>(status), 4); write(bytes, 4, error, 4);
    write(bytes, 8, packages ? packages->size() : 0, 4); write(bytes, 12, static_cast<unsigned>(state), 4);
    write(bytes, 16, detail.size(), 4); const auto encoded = plan ? encodePlan(*plan) : std::vector<std::byte>{};
    write(bytes, 20, encoded.size(), 4); write(bytes, 24, commitDeadline, 8); appendText(bytes, detail);
    if (packages) for (const auto& package : *packages) {
        const auto row = packageRow(package, (package.version == current ? 1u : 0) | (!previous.empty() && package.version == previous ? 2u : 0));
        if (bytes.size() + row.size() > maxPayload) throw Error(Status::io, "Package list exceeds the response limit");
        bytes.insert(bytes.end(), row.begin(), row.end());
    }
    bytes.insert(bytes.end(), encoded.begin(), encoded.end());
    if (bytes.size() > maxPayload) throw Error(Status::io, "Update response exceeds the limit");
    return bytes;
}
}
