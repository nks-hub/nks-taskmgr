#include "consent.hpp"
#include "protocol.hpp"
#include "core/taskmgr_replacement.hpp"
#include <commctrl.h>
#include <shlobj.h>
#include <algorithm>

#pragma comment(linker, "/manifestdependency:\"type='win32' name='Microsoft.Windows.Common-Controls' version='6.0.0.0' processorArchitecture='*' publicKeyToken='6595b64144ccf1df' language='*'\"")

namespace nks::broker {
namespace {
struct Prompt { const Peer& gui; const Peer& bridge; };
HRESULT CALLBACK promptEvent(HWND window, UINT event, WPARAM elapsed, LPARAM, LONG_PTR data)
{
    auto& prompt = *reinterpret_cast<Prompt*>(data);
    if (event == TDN_TIMER && (elapsed >= promptMs || !peerAlive(prompt.gui) || !peerAlive(prompt.bridge)))
        SendMessageW(window, TDM_CLICK_BUTTON, IDCANCEL, 0);
    return S_OK;
}
const wchar_t* actionName(actions::Command command)
{
    using actions::Command;
    switch (command) {
    case Command::query: return L"Načíst prioritu a afinitu";
    case Command::closeWindows: return L"Požádat o zavření oken";
    case Command::terminate: return L"Ukončit procesy bez uložení";
    case Command::terminateTree: return L"Ukončit ověřený strom bez uložení";
    case Command::suspend: return L"Pozastavit procesy";
    case Command::resume: return L"Požádat o obnovení procesů";
    case Command::priority: return L"Změnit prioritu";
    case Command::affinity: return L"Změnit afinitu v jedné skupině";
    case Command::queryCpuSets: return L"Načíst výchozí CPU sets procesu";
    case Command::cpuSets: return L"Změnit výchozí CPU sets procesu";
    case Command::taskManager: return L"Změnit náhradu Správce úloh Windows";
    case Command::closeHandle: return L"Zavřít handle v cizím procesu";
    }
    return L"Neplatná akce";
}
bool duplicate(HANDLE source, Handle& output)
{
    HANDLE raw = nullptr;
    if (!DuplicateHandle(GetCurrentProcess(), source, GetCurrentProcess(), &raw, 0, FALSE, DUPLICATE_SAME_ACCESS)) return false;
    output.reset(raw); return true;
}
bool invisible(wchar_t character)
{
    return character < L' ' || character == 0x7f || character == 0x061c || (character >= 0x200b && character <= 0x200f)
        || (character >= 0x202a && character <= 0x202e) || (character >= 0x2060 && character <= 0x2069) || character == 0xfeff;
}
// Long text keeps both ends because a spoofed path or name usually differs at its end.
std::wstring safeText(std::wstring text, const wchar_t* shortened = L" (zkráceno) ")
{
    if (text.size() > 1024) text = text.substr(0, 640) + L" …" + shortened + L"… " + text.substr(text.size() - 360);
    for (std::size_t i = 0; i < text.size(); ++i) {
        const auto character = text[i];
        if (character >= 0xd800 && character <= 0xdbff && i + 1 < text.size() && text[i + 1] >= 0xdc00 && text[i + 1] <= 0xdfff) { ++i; continue; }
        if (invisible(character) || (character >= 0xd800 && character <= 0xdfff)) text[i] = L' ';
    }
    return text;
}
std::wstring safePath(HANDLE process)
{
    std::wstring path(32768, L'\0'); DWORD size = static_cast<DWORD>(path.size());
    if (!QueryFullProcessImageNameW(process, 0, path.data(), &size)) return L"(cestu nelze přečíst)";
    path.resize(size);
    return safeText(std::move(path), L" (cesta zkrácena)");
}
}

Consent::Consent(const Peer& gui, const Peer& bridge) : gui_(gui), bridge_(bridge)
{
    context_.ownPid = GetCurrentProcessId(); context_.parentPid = gui.key.pid;
    context_.session = gui.token.session; context_.user = gui.token.user; context_.parentKey = gui.key;
    context_.brokerClientKey = bridge.key; context_.protectedPids = {bridge.key.pid};
    context_.ready = duplicate(gui.process.get(), context_.parent) && duplicate(bridge.process.get(), context_.brokerClient);
}

bool Consent::prepare(actions::Command command, std::span<actions::Result> results)
{
    using namespace actions;
    if (!context_.ready || !peerAlive(gui_) || !peerAlive(bridge_)) return false;
    targets_.resize(results.size());
    for (std::size_t i = 0; i < results.size(); ++i) {
        auto& result = results[i]; const auto& key = result.target.key;
        DWORD access = PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE;
        if (command == Command::terminate || command == Command::terminateTree) access |= PROCESS_TERMINATE;
        if (command == Command::suspend || command == Command::resume) access |= 0x0800;
        if (command == Command::priority || command == Command::affinity) access |= PROCESS_SET_INFORMATION;
        if (command == Command::cpuSets) access |= PROCESS_SET_LIMITED_INFORMATION;
        if (command == Command::closeHandle) access |= PROCESS_QUERY_INFORMATION | PROCESS_DUP_HANDLE;
        if (!key.pid || key.pid == 4 || key.pid == context_.ownPid || key.pid == gui_.key.pid || key.pid == bridge_.key.pid) {
            result.reason = key.pid <= 4 ? Reason::systemTarget : Reason::ownOrParent; result.error = ERROR_ACCESS_DENIED;
        } else targets_[i].reset(OpenProcess(access, FALSE, key.pid));
        if (!targets_[i]) {
            if (!result.error) { result.reason = Reason::accessDenied; result.error = GetLastError(); }
            description_ += L"PID " + std::to_wstring(key.pid) + L": nepřístupný nebo zakázaný cíl; nebude změněn.\n";
            continue;
        }
        FILETIME created{}, exited{}, kernel{}, user{};
        const auto identity = queryProcessIdentity(targets_[i].get(), key.generationKind);
        const bool gotTime = GetProcessTimes(targets_[i].get(), &created, &exited, &kernel, &user) != FALSE;
        const auto rawTime = (static_cast<std::uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
        if (identity.state != SampleState::valid || identity.identity != key || !gotTime || rawTime != result.target.created100ns
            || WaitForSingleObject(targets_[i].get(), 0) != WAIT_TIMEOUT) {
            result.outcome = Outcome::identityChanged; result.reason = Reason::identityUnverifiable; result.error = ERROR_RETRY;
            targets_[i].reset(); description_ += L"PID " + std::to_wstring(key.pid) + L": identita již nesouhlasí; nebude změněn.\n"; continue;
        }
        description_ += L"PID " + std::to_wstring(key.pid) + L" · " + (key.generationKind == GenerationKind::sequenceNumber ? L"Sequence " : L"CreateTime ")
            + std::to_wstring(key.generation) + L" · raw " + std::to_wstring(rawTime) + L"\n" + safePath(targets_[i].get()) + L"\n";
    }
    return true;
}

DWORD Consent::prepareReplacement(const actions::Result& target, bool enabled, std::wstring& guiPath)
{
    if (!context_.ready || !peerAlive(gui_) || !peerAlive(bridge_)) return ERROR_INVALID_HANDLE;
    FILETIME created{}, exited{}, kernel{}, user{};
    if (target.target.key != gui_.key || target.target.window || !GetProcessTimes(gui_.process.get(), &created, &exited, &kernel, &user)
        || ((static_cast<std::uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime) != target.target.created100ns) return trustRejected;
    // The path comes from the verified, locked GUI image, never from the request.
    std::wstring path(32768, L'\0');
    const DWORD size = GetFinalPathNameByHandleW(gui_.image.get(), path.data(), static_cast<DWORD>(path.size()), FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    if (!size || size >= path.size()) return trustRejected;
    path.resize(size);
    if (path.starts_with(L"\\\\?\\")) path.erase(0, 4);
    const auto command = replacement::debuggerCommand(path);
    if (command.empty()) return replacementLocation;
    if (enabled) {
        // Every user's Task Manager would start this file, so only administrators may be able to change it.
        PWSTR programFiles = nullptr;
        auto location = replacement::Location::outside;
        if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_ProgramFiles, KF_FLAG_DEFAULT, nullptr, &programFiles)))
            location = replacement::machineLocation(path, programFiles);
        CoTaskMemFree(programFiles);
        if (location == replacement::Location::outside) return replacementLocation;
        if (location != replacement::Location::safe) return replacementAccess;
    }
    const auto plan = replacement::systemPlan(path);
    if (plan.error) return static_cast<DWORD>(plan.error);
    using replacement::Status;
    description_ = L"Trvalá změna celého počítače pro všechny uživatele, platí i po restartu.\n";
    if (enabled) {
        description_ += L"Vstupy spouštějící taskmgr.exe (Ctrl+Shift+Esc, Ctrl+Alt+Del, hlavní panel, Win+X, Win+R) otevřou NKS Task Manager.\n"
            L"Kdykoli se Správce úloh spustí se zvýšenými právy, poběží NKS Task Manager s právy správce.\n";
    }
    description_ += L"\nKlíč: HKLM\\" + std::wstring(replacement::systemKey) + L"\nHodnota: Debugger\n";
    const auto current = safeText(plan.debugger);
    if (enabled) {
        description_ += L"Zapíše se (REG_SZ): " + safeText(command) + L"\n";
        if (plan.status == Status::notRegistered) description_ += L"Současná hodnota: žádná.\n";
        else if (plan.status == Status::registered) description_ += L"Současná hodnota už patří tomuto NKS: " + current + L"\n";
        else if (plan.status == Status::otherNks) description_ += L"Přepíše se starší registrace NKS (nezálohuje se): " + current + L"\n";
        else description_ += L"Přepíše se cizí hodnota: " + current + L"\nUloží se do hodnoty " + std::wstring(replacement::backupValue) + L" pro obnovení.\n";
    } else {
        description_ += L"Současná hodnota: " + (plan.status == Status::notRegistered ? std::wstring(L"žádná") : current) + L"\n";
        if (plan.status == Status::notRegistered) description_ += L"Nic se nezmění.\n";
        else if (plan.status == Status::foreign) description_ += L"Registrace už nepatří NKS, nic se nezmění.\n";
        else if (!plan.hasBackup) description_ += L"Debugger se smaže, taskmgr.exe se bude spouštět jako dřív.\n";
        else if (plan.backupSafe) description_ += L"Obnoví se: " + safeText(plan.backup) + L"\n";
        else description_ += L"Uložená hodnota se neobnoví, protože její program chybí nebo ho mohou měnit i jiní uživatelé než správci: "
            + safeText(plan.backup) + L"\nDebugger se smaže.\n";
    }
    description_ += L"\nNKS: " + safePath(gui_.process.get()) + L"\n";
    guiPath = std::move(path); return ERROR_SUCCESS;
}

bool Consent::confirm(actions::Command command, std::uint64_t argument, std::uint32_t group, std::span<const std::uint32_t> selected,
    const actions::HandleSpec* handle)
{
    TokenIdentity own;
    if (!readToken(GetCurrentProcess(), own) || !own.elevated || own.integrity < SECURITY_MANDATORY_HIGH_RID) return false;
    const bool replacing = command == actions::Command::taskManager;
    std::wstring content = replacing ? std::wstring() : L"Povolujete jeden konkrétní požadavek se zvýšenými právy. Nejde o trvalé povolení.\n"
        L"Zakázané, systémové, kritické a chráněné procesy se nemění. Zrušení čekání nevrací zásah.\n";
    if (command == actions::Command::priority) content += L"Priorita: " + std::to_wstring(argument) + L"\n";
    if (command == actions::Command::affinity) content += L"Skupina: " + std::to_wstring(group) + L", maska: " + std::to_wstring(argument) + L"\n";
    if (command == actions::Command::terminateTree) content += L"Rozsah: tento kořen a nově ověření potomci, nejvýše 256 cílů / 3 průchody / 5 s. Strom není atomický.\n";
    std::wstring cpuDetails;
    if (command == actions::Command::closeHandle && handle) {
        wchar_t value[32]{}; swprintf_s(value, L"0x%llX", static_cast<unsigned long long>(handle->value));
        content += L"Handle " + std::wstring(value) + L" · typ " + safeText(handle->type) + L"\nObjekt: "
            + (handle->flags & actions::verifyHandleName ? handle->name.empty() ? L"(nepojmenovaný)" : safeText(handle->name) : L"(název neověřen)")
            + L"\nCílový proces o zavření neví. Může spadnout, ztratit nebo poškodit neuložená data. Zavření nelze vrátit.\n";
    }
    if (command == actions::Command::cpuSets) {
        content += selected.empty() ? L"Zrušit výchozí výběr CPU sets procesu.\n"
            : L"Nastavit výchozí měkký výběr: " + std::to_wstring(selected.size()) + L" CPU set ID.\n";
        content += L"Tvrdá afinita a výslovné volby vláken zůstanou zachované. Výběr nezaručuje využití konkrétních procesorů.\n";
        if (!selected.empty()) {
            cpuDetails = L"Schvalovaná CPU set ID:\n";
            for (const auto id : selected) { if (cpuDetails.back() != L'\n') cpuDetails += L", "; cpuDetails += std::to_wstring(id); }
        }
    }
    content += replacing ? description_
        : L"\nBoot: " + std::to_wstring(gui_.key.boot.high) + L":" + std::to_wstring(gui_.key.boot.low) + L"\n" + description_;
    TASKDIALOG_BUTTON button{IDYES, L"Povolit tento požadavek"}; Prompt prompt{gui_, bridge_};
    TASKDIALOGCONFIG dialog{}; dialog.cbSize = sizeof(dialog); dialog.dwFlags = TDF_ALLOW_DIALOG_CANCELLATION | TDF_CALLBACK_TIMER;
    dialog.dwCommonButtons = TDCBF_CANCEL_BUTTON; dialog.pszWindowTitle = L"NKS Task Manager · správce";
    dialog.pszMainInstruction = replacing ? (argument == 1 ? L"Nahradit Správce úloh Windows" : L"Obnovit předchozí Správce úloh Windows") : actionName(command); dialog.pszContent = content.c_str(); dialog.pszMainIcon = TD_SHIELD_ICON;
    if (!cpuDetails.empty()) { dialog.pszExpandedInformation = cpuDetails.c_str(); dialog.dwFlags |= TDF_EXPANDED_BY_DEFAULT; }
    dialog.cButtons = 1; dialog.pButtons = &button; dialog.nDefaultButton = IDCANCEL;
    dialog.pfCallback = promptEvent; dialog.lpCallbackData = reinterpret_cast<LONG_PTR>(&prompt);
    int selectedButton = IDCANCEL;
    if (FAILED(TaskDialogIndirect(&dialog, &selectedButton, nullptr, nullptr)) || selectedButton != IDYES || !peerAlive(gui_) || !peerAlive(bridge_)) return false;
    context_.consented = true; return true;
}
}
