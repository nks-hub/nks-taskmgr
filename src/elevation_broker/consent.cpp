#include "consent.hpp"
#include "protocol.hpp"
#include <commctrl.h>
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
    }
    return L"Neplatná akce";
}
bool duplicate(HANDLE source, Handle& output)
{
    HANDLE raw = nullptr;
    if (!DuplicateHandle(GetCurrentProcess(), source, GetCurrentProcess(), &raw, 0, FALSE, DUPLICATE_SAME_ACCESS)) return false;
    output.reset(raw); return true;
}
std::wstring safePath(HANDLE process)
{
    std::wstring path(32768, L'\0'); DWORD size = static_cast<DWORD>(path.size());
    if (!QueryFullProcessImageNameW(process, 0, path.data(), &size)) return L"(cestu nelze přečíst)";
    path.resize(size);
    for (auto& character : path) if (character < L' ' || (character >= 0x202a && character <= 0x202e)
        || (character >= 0x2066 && character <= 0x2069)) character = L' ';
    if (path.size() > 1024) path = path.substr(0, 1024) + L" (cesta zkrácena)";
    return path;
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

bool Consent::confirm(actions::Command command, std::uint64_t argument, std::uint32_t group, std::span<const std::uint32_t> selected)
{
    TokenIdentity own;
    if (!readToken(GetCurrentProcess(), own) || !own.elevated || own.integrity < SECURITY_MANDATORY_HIGH_RID) return false;
    std::wstring content = L"Povolujete jeden konkrétní požadavek se zvýšenými právy. Nejde o trvalé povolení.\n"
        L"Zakázané, systémové, kritické a chráněné procesy se nemění. Zrušení čekání nevrací zásah.\n";
    if (command == actions::Command::priority) content += L"Priorita: " + std::to_wstring(argument) + L"\n";
    if (command == actions::Command::affinity) content += L"Skupina: " + std::to_wstring(group) + L", maska: " + std::to_wstring(argument) + L"\n";
    if (command == actions::Command::terminateTree) content += L"Rozsah: tento kořen a nově ověření potomci, nejvýše 256 cílů / 3 průchody / 5 s. Strom není atomický.\n";
    std::wstring cpuDetails;
    if (command == actions::Command::cpuSets) {
        content += selected.empty() ? L"Zrušit výchozí výběr CPU sets procesu.\n"
            : L"Nastavit výchozí měkký výběr: " + std::to_wstring(selected.size()) + L" CPU set ID.\n";
        content += L"Tvrdá afinita a výslovné volby vláken zůstanou zachované. Výběr nezaručuje využití konkrétních procesorů.\n";
        if (!selected.empty()) {
            cpuDetails = L"Schvalovaná CPU set ID:\n";
            for (const auto id : selected) { if (cpuDetails.back() != L'\n') cpuDetails += L", "; cpuDetails += std::to_wstring(id); }
        }
    }
    content += L"\nBoot: " + std::to_wstring(gui_.key.boot.high) + L":" + std::to_wstring(gui_.key.boot.low) + L"\n" + description_;
    TASKDIALOG_BUTTON button{IDYES, L"Povolit tento požadavek"}; Prompt prompt{gui_, bridge_};
    TASKDIALOGCONFIG dialog{}; dialog.cbSize = sizeof(dialog); dialog.dwFlags = TDF_ALLOW_DIALOG_CANCELLATION | TDF_CALLBACK_TIMER;
    dialog.dwCommonButtons = TDCBF_CANCEL_BUTTON; dialog.pszWindowTitle = L"NKS Task Manager · správce";
    dialog.pszMainInstruction = actionName(command); dialog.pszContent = content.c_str(); dialog.pszMainIcon = TD_SHIELD_ICON;
    if (!cpuDetails.empty()) { dialog.pszExpandedInformation = cpuDetails.c_str(); dialog.dwFlags |= TDF_EXPANDED_BY_DEFAULT; }
    dialog.cButtons = 1; dialog.pButtons = &button; dialog.nDefaultButton = IDCANCEL;
    dialog.pfCallback = promptEvent; dialog.lpCallbackData = reinterpret_cast<LONG_PTR>(&prompt);
    int selectedButton = IDCANCEL;
    if (FAILED(TaskDialogIndirect(&dialog, &selectedButton, nullptr, nullptr)) || selectedButton != IDYES || !peerAlive(gui_) || !peerAlive(bridge_)) return false;
    context_.consented = true; return true;
}
}
