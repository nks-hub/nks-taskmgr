#include "window.hpp"
#include <microsoft.ui.xaml.window.h>
#include <winrt/Windows.Graphics.h>
#include <winrt/Microsoft.UI.h>

namespace nks::candidate {
MainWindow::MainWindow(const std::filesystem::path& directory) {
    window_ = xaml::Window();
    window_.as<IWindowNative>()->get_WindowHandle(&hwnd_);
    window_.Title(L"NKS Task Manager · C++/WinUI");
    window_.AppWindow().Resize({1440, 920});
    const auto icon = directory / L"app.ico";
    if (std::filesystem::is_regular_file(icon)) window_.AppWindow().SetIcon(icon.c_str());
    root_.RequestedTheme(xaml::ElementTheme::Dark);
    root_.Background(brush(0x171d21));
    auto bar = window_.AppWindow().TitleBar();
    bar.BackgroundColor(winrt::Windows::UI::Color{255, 23, 29, 33});
    bar.ForegroundColor(winrt::Windows::UI::Color{255, 233, 238, 240});
    bar.ButtonBackgroundColor(bar.BackgroundColor());
    bar.ButtonForegroundColor(bar.ForegroundColor());
    bar.InactiveBackgroundColor(bar.BackgroundColor());
    bar.ButtonInactiveBackgroundColor(bar.BackgroundColor());
    buildShell(); buildProcesses(); buildPerformance(); buildServices();
    window_.Content(root_);
    native_ = std::make_unique<NativeMonitor>(directory);
    timer_ = window_.DispatcherQueue().CreateTimer();
    timer_.Interval(std::chrono::milliseconds(100));
    timer_.Tick([this](auto&&, auto&&) { tick(); });
    window_.Closed([this](auto&&, auto&&) { timer_.Stop(); native_.reset(); });
    timer_.Start(); navigate(0);
}
void MainWindow::activate() { window_.Activate(); }
void MainWindow::buildShell() {
    column(root_, 88); column(root_);
    controls::StackPanel navigation;
    navigation.Background(brush(0x12181c)); navigation.Spacing(12); navigation.Padding({5, 22, 5, 0});
    constexpr const wchar_t* names[]{L"Výkon", L"Procesy", L"Služby"};
    for (int i = 0; i < 3; ++i) {
        auto item = button(names[i]); item.Width(78); item.Padding({0, 0, 0, 0});
        item.Click([this, i](auto&&, auto&&) { navigate(i); }); navigation.Children().Append(item);
    }
    place(root_, navigation, 0);
    controls::Grid page; page.Padding({22, 20, 22, 12});
    row(page, 62); row(page, 47); row(page); row(page, 28);
    controls::Grid title; column(title); column(title, 90); column(title, 70);
    heading_ = label(L"Výkon", 28); place(title, heading_, 0);
    pause_ = button(L"Pozastavit"); place(title, pause_, 0, 1);
    interval_ = button(L"1 s"); place(title, interval_, 0, 2);
    pause_.Click([this](auto&&, auto&&) {
        try { native_->pause(!paused_); paused_ = !paused_; pause_.Content(winrt::box_value(paused_ ? L"Pokračovat" : L"Pozastavit")); }
        catch (const std::exception& error) { text(footer_, winrt::to_hstring(error.what()).c_str()); }
    });
    interval_.Click([this](auto&&, auto&&) {
        try { native_->interval(intervalFast_ ? 1000 : 250); intervalFast_ = !intervalFast_; interval_.Content(winrt::box_value(intervalFast_ ? L"250 ms" : L"1 s")); }
        catch (const std::exception& error) { text(footer_, winrt::to_hstring(error.what()).c_str()); }
    });
    controls::Grid tools; column(tools); column(tools, 190);
    search_.Height(35); search_.MinHeight(0); search_.FontSize(11); search_.MaxWidth(420);
    search_.HorizontalAlignment(xaml::HorizontalAlignment::Left); search_.PlaceholderText(L"Hledat název nebo PID");
    search_.TextChanged([this](auto&&, auto&&) {
        query_ = lower(std::wstring(search_.Text()));
        if (view_ == 1) updateProcesses(true);
        if (view_ == 2) updateServices(true);
    });
    place(tools, search_, 0);
    mode_.Height(35); mode_.MinHeight(0); mode_.FontSize(11); mode_.HorizontalAlignment(xaml::HorizontalAlignment::Stretch);
    for (auto name : {L"Všechny procesy", L"Aktivní procesy", L"Strom procesů"}) mode_.Items().Append(winrt::box_value(name));
    mode_.SelectedIndex(0);
    mode_.SelectionChanged([this](auto&&, auto&&) { updateProcesses(true); });
    place(tools, mode_, 0, 1);
    place(page, title, 0); place(page, tools, 1); place(page, body_, 2);
    footer_ = label(L"Načítám živá data…", 9, 0x95a4ad); place(page, footer_, 3);
    place(root_, page, 0, 1);
    root_.KeyDown([this](auto&&, xaml::Input::KeyRoutedEventArgs const& args) {
        if (args.Key() == winrt::Windows::System::VirtualKey::F3 && view_ != 0) {
            search_.Focus(xaml::FocusState::Keyboard); args.Handled(true);
        }
    });
}
void MainWindow::navigate(int view) {
    if (!native_) return;
    view_ = view; held_ = false;
    native_->services(view == 2);
    processPage_.Visibility(view == 1 ? xaml::Visibility::Visible : xaml::Visibility::Collapsed);
    performancePage_.Visibility(view == 0 ? xaml::Visibility::Visible : xaml::Visibility::Collapsed);
    servicePage_.Visibility(view == 2 ? xaml::Visibility::Visible : xaml::Visibility::Collapsed);
    search_.Visibility(view == 0 ? xaml::Visibility::Collapsed : xaml::Visibility::Visible);
    mode_.Visibility(view == 1 ? xaml::Visibility::Visible : xaml::Visibility::Collapsed);
    text(heading_, view == 0 ? L"Výkon" : view == 1 ? L"Procesy" : L"Služby");
    query_ = lower(std::wstring(search_.Text()));
    if (view == 1) updateProcesses(true);
    if (view == 2) updateServices(true);
}
void MainWindow::tick() {
    if (!native_ || IsIconic(hwnd_)) return;
    try {
        native_->poll(snapshot_);
        if (view_ == 1) updateProcesses();
        if (view_ == 0) updatePerformance();
        if (view_ == 2) updateServices();
        std::wstring status = paused_ ? L"Pozastaveno" : L"Živá data";
        status += L" · " + std::to_wstring(snapshot_.processes.size()) + L" procesů";
        if (held_ && view_ == 1) status += L" · pořadí přidrženo pod ukazatelem";
        auto append = [&](const std::wstring& error) { if (!error.empty()) status += L" · " + error; };
        append(snapshot_.processError); append(snapshot_.resourceError);
        if (view_ == 2) append(snapshot_.serviceError);
        if (snapshot_.lost || snapshot_.captureErrors)
            append(L"Historie: " + std::to_wstring(snapshot_.lost) + L" neuchovaných vzorků, " + std::to_wstring(snapshot_.captureErrors) + L" chyb");
        text(footer_, status);
    } catch (const std::exception& error) {
        text(footer_, L"Chyba nativního rozhraní: " + std::wstring(winrt::to_hstring(error.what())));
    } catch (const winrt::hresult_error& error) { text(footer_, L"Chyba zobrazení: " + std::wstring(error.message())); }
}
void MainWindow::bindContainers(controls::ListView const& list,
    std::map<std::wstring, winrt::weak_ref<controls::Grid>>& containers,
    std::function<void(const std::wstring&, controls::Grid const&)> render) {
    list.ContainerContentChanging([&containers, render = std::move(render)](auto&&,
        controls::ContainerContentChangingEventArgs const& args) {
        auto grid = args.ItemContainer().ContentTemplateRoot().try_as<controls::Grid>();
        if (!grid) return;
        const auto previous = token(grid.Tag());
        if (!previous.empty()) containers.erase(previous);
        if (args.InRecycleQueue()) { grid.Tag(nullptr); return; }
        const auto key = token(args.Item());
        grid.Tag(winrt::box_value(key)); containers.insert_or_assign(key, winrt::make_weak(grid));
        render(key, grid);
        xaml::Automation::AutomationProperties::SetName(args.ItemContainer(), xaml::Automation::AutomationProperties::GetName(grid));
        args.Handled(true);
    });
}
void MainWindow::refreshContainers(std::map<std::wstring, winrt::weak_ref<controls::Grid>>& containers,
    const std::function<void(const std::wstring&, controls::Grid const&)>& render) {
    for (auto it = containers.begin(); it != containers.end();) {
        if (auto grid = it->second.get()) { render(it->first, grid); ++it; }
        else it = containers.erase(it);
    }
}
bool MainWindow::reconcile(controls::ListView const& list, Items const& items,
    std::map<std::wstring, IInspectable>& objects, const std::vector<std::wstring>& keys) {
    bool same = items.Size() == keys.size();
    if (same) for (uint32_t i = 0; i < items.Size(); ++i) if (token(items.GetAt(i)) != keys[i]) { same = false; break; }
    if (same) return false;
    std::set<std::wstring> selected;
    for (const auto& item : list.SelectedItems()) selected.insert(token(item));
    const auto primary = token(list.SelectedItem());
    const auto scroll = scrollViewer(list);
    const auto offset = scroll ? scroll.VerticalOffset() : 0.0;
    std::vector<IInspectable> next; next.reserve(keys.size());
    std::set<std::wstring> keep;
    for (const auto& key : keys) {
        keep.insert(key);
        auto [item, inserted] = objects.try_emplace(key, nullptr);
        if (inserted) item->second = winrt::box_value(key);
        next.push_back(item->second);
    }
    updating_ = true;
    items.ReplaceAll(next);
    list.SelectedItems().Clear();
    if (!primary.empty() && keep.contains(primary) && selected.contains(primary)) list.SelectedItem(objects.at(primary));
    for (const auto& key : selected)
        if (key != primary && keep.contains(key)) list.SelectedItems().Append(objects.at(key));
    updating_ = false;
    for (auto it = objects.begin(); it != objects.end();) {
        if (!keep.contains(it->first)) it = objects.erase(it); else ++it;
    }
    if (scroll) scroll.ChangeView(nullptr, winrt::box_value(offset).as<foundation::IReference<double>>(), nullptr, true);
    return true;
}
}
