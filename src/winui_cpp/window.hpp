#pragma once
#include "native_monitor.hpp"
#include "charts.hpp"
#include <functional>
#include <memory>

namespace nks::candidate {
class MainWindow {
public:
    explicit MainWindow(const std::filesystem::path& directory);
    void activate();
private:
    xaml::Window window_{nullptr};
    controls::Grid root_, body_, processPage_, performancePage_, servicePage_;
    controls::Grid performanceBody_, chartBox_;
    controls::TextBlock heading_{nullptr}, footer_{nullptr};
    controls::TextBox search_;
    controls::Button pause_{nullptr}, interval_{nullptr};
    controls::ComboBox mode_;
    controls::ListView processList_, resourceList_, serviceList_;
    controls::TextBlock processFacts_{nullptr}, resourceTitle_{nullptr}, resourceDescription_{nullptr};
    controls::TextBlock resourceValue_{nullptr}, resourceFacts_{nullptr}, serviceFacts_{nullptr};
    shapes::Path primary_, secondary_;
    Items processItems_{winrt::single_threaded_observable_vector<IInspectable>()};
    Items resourceItems_{winrt::single_threaded_observable_vector<IInspectable>()};
    Items serviceItems_{winrt::single_threaded_observable_vector<IInspectable>()};
    std::map<std::wstring, Process> processes_;
    std::map<std::wstring, Resource> resources_;
    std::map<std::wstring, Service> services_;
    std::map<std::wstring, winrt::weak_ref<controls::Grid>> processContainers_, resourceContainers_, serviceContainers_;
    std::map<std::wstring, IInspectable> processObjects_, resourceObjects_, serviceObjects_;
    std::map<std::wstring, int> depths_;
    std::set<std::wstring> collapsed_, branches_;
    Snapshot snapshot_;
    std::unique_ptr<NativeMonitor> native_;
    winrt::Microsoft::UI::Dispatching::DispatcherQueueTimer timer_{nullptr};
    HWND hwnd_{};
    int view_{0}, sortColumn_{0}, range_{60};
    bool descending_{}, paused_{}, held_{}, updating_{}, intervalFast_{};
    std::wstring query_, selectedResource_, selectedService_;
    void buildShell();
    void buildProcesses();
    void buildPerformance();
    void buildServices();
    void navigate(int view);
    void tick();
    void updateProcesses(bool force = false);
    void updatePerformance();
    void updateServices(bool force = false);
    void selection();
    void serviceSelection();
    void renderProcess(const std::wstring& key, controls::Grid const& grid);
    void renderResource(const std::wstring& key, controls::Grid const& grid);
    void renderService(const std::wstring& key, controls::Grid const& grid);
    std::vector<std::wstring> processOrder();
    const History* history(const std::wstring& resource, uint32_t channel = 0) const;
    int64_t chartTime(const History* first, const History* second = nullptr) const;
    void toggleTree();
    void refreshContainers(std::map<std::wstring, winrt::weak_ref<controls::Grid>>& containers,
        const std::function<void(const std::wstring&, controls::Grid const&)>& render);
    void bindContainers(controls::ListView const& list, std::map<std::wstring, winrt::weak_ref<controls::Grid>>& containers,
        std::function<void(const std::wstring&, controls::Grid const&)> render);
    bool reconcile(controls::ListView const& list, Items const& items, std::map<std::wstring, IInspectable>& objects,
        const std::vector<std::wstring>& keys);
};
}
