#include <phnt_windows.h>
#include <phnt.h>
#include "execute.hpp"

namespace nks::update {
void install(Cache& cache, Context& context, Plan& plan, Image& package)
{
    journal(cache, plan, State::waitingGui, 2);
    if (WaitForSingleObject(context.gui.process.get(), 30000) != WAIT_OBJECT_0) { journal(cache, plan, State::guiAlive, 4, ERROR_TIMEOUT); return; }
    const auto scope = classifyScope(context.gui); requireScope(scope);
    if (scope.mode != plan.mode() || !sameRoot(scope.id, plan.root)) throw Error(Status::rejected, "Application root or mode changed before installation");
    context.gui.image.reset(); context.installed.lease.image.reset();
    if (!matches(plan.package, package) || package.version != plan.selectedVersion()) throw Error(Status::verificationFailed, "Installer proof changed before execution");
    std::wstring command = L"\"" + package.lease.path + L"\" /CURRENTUSER /NOCLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /NORESTART /NKSTRANSACTION=1";
    if (plan.mode() == InstallMode::portable) command += L" /PORTABLE=1";
    command += L" /DIR=\"" + plan.installDir() + L"\"";
    wchar_t system[32768]{}; const auto systemSize = GetSystemDirectoryW(system, static_cast<UINT>(std::size(system)));
    if (!systemSize || systemSize >= std::size(system)) throw Error(Status::io, "System directory is unavailable");
    STARTUPINFOW startup{}; startup.cb = sizeof(startup); PROCESS_INFORMATION process{};
    if (!CreateProcessW(package.lease.path.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED, nullptr, system, &startup, &process)) {
        journal(cache, plan, State::installerFailed, 4, GetLastError()); return;
    }
    broker::Handle installer(process.hProcess), thread(process.hThread);
    const auto identity = queryProcessIdentity(installer.get(), GenerationKind::creationTime);
    HANDLE image = package.lease.image.get();
    if (identity.state != SampleState::valid || identity.identity.boot != context.self.key.boot
        || NtQueryInformationProcess(installer.get(), ProcessImageFileMapping, &image, sizeof(image), nullptr) != STATUS_SUCCESS) {
        TerminateProcess(installer.get(), ERROR_OPERATION_ABORTED);
        journal(cache, plan, State::unknown, 4, ERROR_INVALID_DATA); return;
    }
    try {
        // The exact new process is still suspended until its identity is durably published.
        journal(cache, plan, State::installing, 3, 0, 0, &identity.identity);
    } catch (...) { TerminateProcess(installer.get(), ERROR_OPERATION_ABORTED); throw; }
    const auto resumed = ResumeThread(thread.get());
    if (resumed != 1) { journal(cache, plan, State::unknown, 4, resumed == static_cast<DWORD>(-1) ? GetLastError() : ERROR_INVALID_STATE, 0, &identity.identity); return; }
    if (WaitForSingleObject(installer.get(), 30 * 60 * 1000) != WAIT_OBJECT_0) { journal(cache, plan, State::unknown, 4, ERROR_TIMEOUT, 0, &identity.identity); return; }
    DWORD exit = 0;
    if (!GetExitCodeProcess(installer.get(), &exit)) { journal(cache, plan, State::unknown, 4, GetLastError(), 0, &identity.identity); return; }
    if (exit != 0) { journal(cache, plan, State::installerFailed, 4, 0, exit, &identity.identity); return; }
    try {
        auto installed = openImage(plan.installDir() + L"\\NksTaskMgr.WinUI.exe", Role::application);
        const auto after = classifyScope(context.gui); requireScope(after);
        if (installed.version != plan.selectedVersion() || after.mode != plan.mode() || !sameRoot(after.id, plan.root)) {
            journal(cache, plan, State::verificationFailed, 4, ERROR_REVISION_MISMATCH, exit, &identity.identity); return;
        }
        journal(cache, plan, State::verified, 4, 0, exit, &identity.identity, &installed);
    } catch (const Error& error) { journal(cache, plan, State::verificationFailed, 4, error.windows, exit, &identity.identity); }
}
}
