#include "window.hpp"
#include <winrt/Microsoft.UI.Xaml.XamlTypeInfo.h>
#include <winrt/Windows.UI.Xaml.Interop.h>
#include <winrt/Microsoft.UI.Xaml.Controls.h>

namespace nks::candidate {
struct App : xaml::ApplicationT<App, xaml::Markup::IXamlMetadataProvider> {
    std::unique_ptr<MainWindow> main;
    xaml::XamlTypeInfo::XamlControlsXamlMetaDataProvider provider;
    void OnLaunched(xaml::LaunchActivatedEventArgs const&) {
        Resources().MergedDictionaries().Append(controls::XamlControlsResources());
        std::wstring path(32768, L'\0');
        const auto length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
        if (!length || length >= path.size()) winrt::throw_last_error();
        path.resize(length);
        main = std::make_unique<MainWindow>(std::filesystem::path(path).parent_path());
        main->activate();
    }
    xaml::Markup::IXamlType GetXamlType(winrt::Windows::UI::Xaml::Interop::TypeName const& type) { return provider.GetXamlType(type); }
    xaml::Markup::IXamlType GetXamlType(winrt::hstring const& name) { return provider.GetXamlType(name); }
    winrt::com_array<xaml::Markup::XmlnsDefinition> GetXmlnsDefinitions() { return provider.GetXmlnsDefinitions(); }
};
}
int WINAPI wWinMain(HINSTANCE, HINSTANCE, LPWSTR, int) {
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_APPLICATION_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    try {
        winrt::init_apartment(winrt::apartment_type::single_threaded);
        winrt::Microsoft::UI::Xaml::Application::Start([](auto&&) { winrt::make<nks::candidate::App>(); });
        return 0;
    } catch (const winrt::hresult_error& error) {
        MessageBoxW(nullptr, error.message().c_str(), L"NKS Task Manager · C++/WinUI", MB_ICONERROR);
        return 1;
    } catch (const std::exception& error) {
        const auto message = winrt::to_hstring(error.what());
        MessageBoxW(nullptr, message.c_str(), L"NKS Task Manager · C++/WinUI", MB_ICONERROR);
        return 1;
    }
}
