#include "ui/desktop.hpp"

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR arguments, int show)
{
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    nks::Desktop desktop;
    return desktop.run(instance, show, arguments && std::wstring(arguments) == L"--smoke");
}
