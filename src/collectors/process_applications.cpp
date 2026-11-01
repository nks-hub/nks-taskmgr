#include "process_details.hpp"
#include <appmodel.h>
#include <dwmapi.h>
#include <algorithm>
#include <array>
#include <exception>

namespace nks {
namespace {

ProcessIdentityResult identity(HANDLE process, GenerationKind preference)
{
    auto value = queryProcessIdentity(process, preference);
    if (preference == GenerationKind::sequenceNumber && value.state == SampleState::unsupported)
        value = queryProcessIdentity(process, GenerationKind::creationTime);
    return value;
}

struct WindowProcess {
    DetailHandle handle;
    ProcessIdentity key;
};

bool windowProcess(HWND window, const ProcessDetailsRequest& request, WindowProcess& process)
{
    DWORD pid = 0;
    if (!GetWindowThreadProcessId(window, &pid) || !pid) return false;
    process.handle.reset(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, pid));
    if (!process.handle || WaitForSingleObject(process.handle.get(), 0) != WAIT_TIMEOUT) return false;
    const auto actual = identity(process.handle.get(), request.target.generationKind);
    if (actual.state != SampleState::valid || actual.identity.pid != pid || actual.identity.boot != request.target.boot) return false;
    process.key = actual.identity; return true;
}

bool stillOwns(HWND window, const WindowProcess& process)
{
    DWORD pid = 0;
    if (!GetWindowThreadProcessId(window, &pid) || pid != process.key.pid || WaitForSingleObject(process.handle.get(), 0) != WAIT_TIMEOUT) return false;
    const auto actual = queryProcessIdentity(process.handle.get(), process.key.generationKind);
    return actual.state == SampleState::valid && actual.identity == process.key;
}

template<class Query>
SampleState appText(HANDLE process, Query query, std::wstring& value)
{
    std::array<wchar_t, 512> buffer{};
    UINT32 length = static_cast<UINT32>(buffer.size());
    const auto error = query(process, &length, buffer.data());
    if (error == APPMODEL_ERROR_NO_PACKAGE || error == APPMODEL_ERROR_NO_APPLICATION) return SampleState::unsupported;
    if (error == ERROR_ACCESS_DENIED) return SampleState::accessDenied;
    if (error != ERROR_SUCCESS || !length || length > buffer.size() || buffer[length - 1] != L'\0') return SampleState::unavailable;
    value.assign(buffer.data(), length - 1);
    return value.empty() ? SampleState::unavailable : SampleState::valid;
}

struct CoreWindows {
    DWORD framePid{};
    HWND content{};
    DWORD contentPid{};
    unsigned examined{};
    bool ambiguous{};
};

BOOL CALLBACK childWindow(HWND window, LPARAM parameter) noexcept
{
    auto& found = *reinterpret_cast<CoreWindows*>(parameter);
    if (++found.examined > 4096) { found.ambiguous = true; return FALSE; }
    std::array<wchar_t, 96> name{};
    if (!IsWindowVisible(window) || !GetClassNameW(window, name.data(), static_cast<int>(name.size()))
        || std::wstring_view(name.data()) != L"Windows.UI.Core.CoreWindow") return TRUE;
    DWORD pid = 0; GetWindowThreadProcessId(window, &pid);
    if (!pid || pid == found.framePid) return TRUE;
    if (found.contentPid && found.contentPid != pid) { found.ambiguous = true; return FALSE; }
    found.content = window; found.contentPid = pid;
    return TRUE;
}

struct Inventory {
    const ProcessDetailsRequest& request;
    ProcessDetailsResult& result;
    std::exception_ptr error;
    unsigned examined{};

    bool read(HWND window)
    {
        if (++examined > 32768 || result.applications.size() == details::maximumApplications) { result.flags |= details::partialMetrics; return false; }
        if (!IsWindowVisible(window) || (GetWindowLongPtrW(window, GWL_EXSTYLE) & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE))) return true;
        std::array<wchar_t, 96> className{};
        GetClassNameW(window, className.data(), static_cast<int>(className.size()));
        const std::wstring_view windowClass(className.data());
        if (windowClass == L"Progman" || windowClass == L"WorkerW" || windowClass == L"Shell_TrayWnd" || windowClass == L"Shell_SecondaryTrayWnd") return true;
        DWORD cloaked = 0;
        if (SUCCEEDED(DwmGetWindowAttribute(window, DWMWA_CLOAKED, &cloaked, sizeof(cloaked)))) { if (cloaked) return true; }
        else result.flags |= details::partialMetrics;
        std::array<wchar_t, 512> caption{};
        const auto length = GetWindowTextW(window, caption.data(), static_cast<int>(caption.size()));
        ProcessApplicationDetail app;
        app.window = reinterpret_cast<std::uintptr_t>(window); app.title.assign(caption.data(), static_cast<std::size_t>(length));
        if (length == static_cast<int>(caption.size() - 1)) {
            result.flags |= details::partialMetrics;
            if (app.title.back() >= 0xd800 && app.title.back() <= 0xdbff) app.title.pop_back();
        }
        WindowProcess frame;
        if (!windowProcess(window, request, frame)) { result.flags |= details::partialMetrics; return true; }
        WindowProcess content;
        HWND contentWindow = window;
        WindowProcess* owner = &frame;
        if (windowClass == L"ApplicationFrameWindow") {
            CoreWindows children{frame.key.pid};
            EnumChildWindows(window, childWindow, reinterpret_cast<LPARAM>(&children));
            if (children.ambiguous || !children.content || !IsChild(window, children.content)
                || !windowProcess(children.content, request, content)) { result.flags |= details::partialMetrics; return true; }
            owner = &content; contentWindow = children.content; app.host = frame.key;
        }
        app.owner = owner->key;
        app.appIdState = appText(owner->handle.get(), GetApplicationUserModelId, app.appId);
        app.packageState = appText(owner->handle.get(), GetPackageFamilyName, app.package);
        if (!stillOwns(window, frame) || !stillOwns(contentWindow, *owner) || (owner == &content && !IsChild(window, contentWindow))) {
            result.flags |= details::partialMetrics; return true;
        }
        if (app.appIdState != SampleState::valid && app.appIdState != SampleState::unsupported
            || app.packageState != SampleState::valid && app.packageState != SampleState::unsupported) result.flags |= details::partialMetrics;
        result.applications.push_back(std::move(app)); return true;
    }
};

BOOL CALLBACK topWindow(HWND window, LPARAM parameter) noexcept
{
    auto& inventory = *reinterpret_cast<Inventory*>(parameter);
    try { return inventory.read(window) ? TRUE : FALSE; }
    catch (...) { inventory.error = std::current_exception(); return FALSE; }
}
}

void readProcessApplications(const ProcessDetailsRequest& request, ProcessDetailsResult& result)
{
    const auto before = queryProcessIdentity(GetCurrentProcess(), GenerationKind::creationTime);
    if (before.state != SampleState::valid) { result.state = details::ResultState::unavailable; return; }
    if (before.identity.boot != request.target.boot) { result.state = details::ResultState::targetChanged; return; }
    Inventory inventory{request, result};
    SetLastError(ERROR_SUCCESS);
    const bool enumerated = EnumWindows(topWindow, reinterpret_cast<LPARAM>(&inventory)) != FALSE;
    if (inventory.error) std::rethrow_exception(inventory.error);
    if (!enumerated && !(result.flags & details::partialMetrics)) { result.state = details::ResultState::error; result.win32Error = GetLastError(); return; }
    const auto after = queryProcessIdentity(GetCurrentProcess(), GenerationKind::creationTime);
    result.state = after.state == SampleState::valid && after.identity.boot == request.target.boot ? details::ResultState::ok : details::ResultState::targetChanged;
}
}
