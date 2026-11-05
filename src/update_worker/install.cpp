#include <phnt_windows.h>
#include <phnt.h>
#include "execute.hpp"
#include <array>

namespace nks::update {
namespace {
// CreateProcess and Inno Setup reject volume GUID paths; the same location under its drive letter is accepted.
// The started installer is still verified against the held package by its mapped image.
std::wstring driveLetterPath(const std::wstring& path)
{
    const std::wstring prefix = L"\\\\?\\Volume{";
    const auto end = path.find(L"}\\");
    if (!path.starts_with(prefix) || end == std::wstring::npos) return path;
    std::array<wchar_t, 1024> names{}; DWORD length = 0;
    if (!GetVolumePathNamesForVolumeNameW(path.substr(0, end + 2).c_str(), names.data(), static_cast<DWORD>(names.size()), &length) || names[0] == L'\0')
        return path;
    return std::wstring(names.data()) + path.substr(end + 2);
}
}

void install(Cache& cache, Context& context, Plan& plan, Image& package)
{
    journal(cache, plan, State::waitingGui, 2);
    if (WaitForSingleObject(context.gui.process.get(), 30000) != WAIT_OBJECT_0) { journal(cache, plan, State::guiAlive, 4, ERROR_TIMEOUT); return; }
    const auto scope = classifyScope(context.gui); requireScope(scope);
    if (scope.mode != plan.mode() || !sameRoot(scope.id, plan.root)) throw Error(Status::rejected, "Application root or mode changed before installation");
    // Release the verified pins on the application folder too: they share only reading, and the installer must
    // rename and replace files in it (Inno Setup otherwise aborts with a sharing violation on the first file).
    context.gui.image.reset(); context.installed.lease.image.reset();
    context.gui.directories.clear(); context.installed.lease.directories.clear();
    if (!matches(plan.package, package) || package.version != plan.selectedVersion()) throw Error(Status::verificationFailed, "Installer proof changed before execution");
    const auto installer_path = driveLetterPath(package.lease.path);
    // The user already confirmed the plan; /SILENT shows only progress, with no language or wizard questions.
    // /LOG keeps the Inno Setup log in the user TEMP folder for a failed update.
    std::wstring command = L"\"" + installer_path + L"\" /SILENT /SUPPRESSMSGBOXES /LOG /CURRENTUSER /NOCLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /NORESTART /NKSTRANSACTION=1";
    if (plan.mode() == InstallMode::portable) command += L" /PORTABLE=1";
    command += L" /DIR=\"" + driveLetterPath(plan.installDir()) + L"\"";
    wchar_t system[32768]{}; const auto systemSize = GetSystemDirectoryW(system, static_cast<UINT>(std::size(system)));
    if (!systemSize || systemSize >= std::size(system)) throw Error(Status::io, "System directory is unavailable");
    STARTUPINFOW startup{}; startup.cb = sizeof(startup); PROCESS_INFORMATION process{};
    if (!CreateProcessW(installer_path.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED, nullptr, system, &startup, &process)) {
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
