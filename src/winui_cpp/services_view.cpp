#include "window.hpp"

namespace nks::candidate {
void MainWindow::buildServices() {
    row(servicePage_); row(servicePage_, 170);
    setupList(serviceList_, 56); serviceList_.SelectionMode(controls::ListViewSelectionMode::Single);
    serviceList_.ItemsSource(serviceItems_);
    bindContainers(serviceList_, serviceContainers_, [this](auto& key, auto& grid) { renderService(key, grid); });
    serviceList_.SelectionChanged([this](auto&&, auto&&) {
        if (updating_) return;
        selectedService_ = token(serviceList_.SelectedItem());
        if (native_) native_->selectService(selectedService_);
        serviceSelection();
    });
    place(servicePage_, serviceList_, 0);
    serviceFacts_ = label(L"Vyberte službu pro konfiguraci a závislosti.", 11, 0x95a4ad);
    serviceFacts_.TextWrapping(xaml::TextWrapping::Wrap); serviceFacts_.IsTextSelectionEnabled(true);
    controls::ScrollViewer detail; detail.Content(serviceFacts_); place(servicePage_, detail, 1);
    body_.Children().Append(servicePage_);
}
void MainWindow::updateServices(bool force) {
    if (!serviceFacts_) return;
    if (force || snapshot_.serviceChanged) {
        services_.clear(); std::vector<std::wstring> keys;
        for (const auto& service : snapshot_.services) {
            services_[service.name] = service;
            if (!query_.empty() && lower(service.name + L" " + service.title + L" " + service.account).find(query_) == std::wstring::npos
                && std::to_wstring(service.pid).find(query_) == std::wstring::npos) continue;
            keys.push_back(service.name);
        }
        std::sort(keys.begin(), keys.end(), [](const auto& a, const auto& b) { return lower(a) < lower(b); });
        reconcile(serviceList_, serviceItems_, serviceObjects_, keys);
        selectedService_ = token(serviceList_.SelectedItem());
    }
    refreshContainers(serviceContainers_, [this](auto& key, auto& grid) { renderService(key, grid); });
    serviceSelection();
}
void MainWindow::renderService(const std::wstring& key, controls::Grid const& grid) {
    auto found = services_.find(key); if (found == services_.end()) return;
    const auto& service = found->second;
    if (grid.Children().Size() == 0) {
        column(grid); column(grid, 140); column(grid, 65); column(grid, 150);
        grid.MinWidth(650); grid.BorderBrush(brush(0x2c363d)); grid.BorderThickness({0, 0, 0, 1});
        for (int i = 0; i < 4; ++i) { auto block = label(L"", 11); block.Margin({12, 0, 12, 0}); place(grid, block, 0, i); }
    }
    auto state = serviceState(service.state);
    if (!paused_ && snapshot_.serviceTime > 0 && snapshot_.now - snapshot_.serviceTime > 8000000000LL) state += L" · zastaralé";
    std::array<std::wstring, 4> values{service.title + L" (" + service.name + L")", state,
        service.pid ? std::to_wstring(service.pid) : L"—", service.configError ? L"Konfigurace nedostupná" : service.account};
    for (uint32_t i = 0; i < values.size(); ++i) text(grid.Children().GetAt(i).as<controls::TextBlock>(), values[i]);
    xaml::Automation::AutomationProperties::SetName(grid, values[0] + L", " + values[1]);
}
void MainWindow::serviceSelection() {
    auto found = services_.find(selectedService_);
    if (found == services_.end()) { text(serviceFacts_, L"Vyberte službu pro konfiguraci a závislosti."); return; }
    const auto& service = found->second;
    std::wstring info = service.title + L" · " + service.name + L" · " + serviceState(service.state)
        + L"\nÚčet: " + (service.configError ? L"nedostupný (" + std::to_wstring(service.configError) + L")" : service.account)
        + L"\nPříkaz: " + service.command + L"\nZávislosti: ";
    for (const auto& value : service.dependencies) info += value + L"; ";
    if (service.pid) {
        size_t shared{};
        for (const auto& [name, other] : services_) { (void)name; if (other.pid == service.pid) ++shared; }
        info += L"\nHlášený PID " + std::to_wstring(service.pid) + L" · služby ve stejném hlášeném hostiteli: " + std::to_wstring(shared);
    }
    const auto& detail = snapshot_.detail;
    if (detail.name == service.name) {
        info += L"\n" + detail.description;
        info += L"\nVazba hostitele: " + (detail.hostState == NKS_SAMPLE_VALID ? detail.host.text() : sampleState(detail.hostState));
        if (!detail.error.empty()) info += L"\n" + detail.error;
    } else info += L"\nOvěření hostitele se načítá; samotný PID není celoživotní identita.";
    text(serviceFacts_, info);
}
}
