#include "transport.hpp"
#include "core/helper_policy.hpp"
#include <thread>
#include <string_view>

int wmain(int argc, wchar_t** argv)
{
    if (!nks::initializeHelperProcess()) return 2;
    // This also bounds malformed stdin, ShellExecuteEx/UAC, and provider calls outside pipe deadlines.
    std::thread([] {
        Sleep(static_cast<DWORD>(nks::broker::startupMs + nks::broker::promptMs + nks::broker::operationMs + 10000));
        TerminateProcess(GetCurrentProcess(), ERROR_TIMEOUT);
    }).detach();
    if (argc == 2 && std::wstring_view(argv[1]) == L"--stdio") return nks::broker::bridge();
    if (argc != 4 || std::wstring_view(argv[1]) != L"--serve") return 2;
    const std::wstring_view suffix(argv[2]), pidText(argv[3]);
    if (suffix.size() != 32 || pidText.empty() || pidText.size() > 10) return 2;
    for (wchar_t c : suffix) if (!((c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f'))) return 2;
    std::uint64_t pid = 0;
    for (wchar_t c : pidText) { if (c < L'0' || c > L'9') return 2; pid = pid * 10 + c - L'0'; }
    if (!pid || pid > MAXDWORD) return 2;
    return nks::broker::serve(L"\\\\.\\pipe\\NksTaskMgr-" + std::wstring(suffix), static_cast<DWORD>(pid));
}
