#include "window.hpp"

namespace nks::candidate {
void MainWindow::buildPerformance() {
    column(performanceBody_, 265); column(performanceBody_);
    setupList(resourceList_, 57); resourceList_.ItemsSource(resourceItems_);
    resourceList_.SelectionMode(controls::ListViewSelectionMode::Single);
    bindContainers(resourceList_, resourceContainers_, [this](auto& key, auto& grid) { renderResource(key, grid); });
    resourceList_.SelectionChanged([this](auto&&, auto&&) {
        if (!updating_) { selectedResource_ = token(resourceList_.SelectedItem()); updatePerformance(); }
    });
    place(performanceBody_, resourceList_, 0);
    controls::Grid detail; detail.Padding({22, 0, 0, 0});
    row(detail, 42); row(detail, 36); row(detail, 75); row(detail, 42); row(detail, 260); row(detail);
    resourceTitle_ = label(L"Načítám prostředky…", 20); place(detail, resourceTitle_, 0);
    resourceDescription_ = label(L"", 10, 0x95a4ad); place(detail, resourceDescription_, 1);
    resourceValue_ = label(L"Načítám…", 42, 0x80c5c0); place(detail, resourceValue_, 2);
    controls::StackPanel ranges; ranges.Orientation(controls::Orientation::Horizontal); ranges.Spacing(6);
    for (const auto seconds : {60, 600}) {
        auto item = button(seconds == 60 ? L"60 sekund" : L"10 minut");
        item.Click([this, seconds](auto&&, auto&&) { range_ = seconds; updatePerformance(); }); ranges.Children().Append(item);
    }
    place(detail, ranges, 3);
    chartBox_.Background(brush(0x12181c)); chartBox_.MinWidth(80);
    for (int i = 1; i < 4; ++i) {
        shapes::Line line; line.X1(0); line.Y1(i * 65); line.Y2(i * 65);
        line.Stroke(brush(0x2c363d)); line.StrokeThickness(1);
        chartBox_.Children().Append(line);
    }
    primary_.Stroke(brush(0x80c5c0)); primary_.StrokeThickness(1.8);
    secondary_.Stroke(brush(0xdfaf76)); secondary_.StrokeThickness(1.4);
    chartBox_.Children().Append(primary_); chartBox_.Children().Append(secondary_);
    chartBox_.SizeChanged([this](auto&&, auto&&) { updatePerformance(); });
    place(detail, chartBox_, 4);
    resourceFacts_ = label(L"", 11, 0x95a4ad); resourceFacts_.TextWrapping(xaml::TextWrapping::Wrap);
    resourceFacts_.IsTextSelectionEnabled(true); resourceFacts_.Margin({0, 16, 0, 0});
    resourceFacts_.VerticalAlignment(xaml::VerticalAlignment::Top); place(detail, resourceFacts_, 5);
    controls::ScrollViewer vertical; vertical.Content(detail);
    vertical.HorizontalScrollBarVisibility(controls::ScrollBarVisibility::Disabled);
    place(performanceBody_, vertical, 0, 1);
    performancePage_.Children().Append(performanceBody_); body_.Children().Append(performancePage_);
    root_.SizeChanged([this](auto&&, xaml::SizeChangedEventArgs const& args) {
        const bool compact = args.NewSize().Width < 950;
        performanceBody_.ColumnDefinitions().GetAt(0).Width({compact ? 180.0 : 265.0, xaml::GridUnitType::Pixel});
    });
}
const History* MainWindow::history(const std::wstring& resource, uint32_t channel) const {
    for (const auto& [id, series] : snapshot_.series) {
        (void)id;
        if (series.resource == resource && series.channel == channel) return &series.history;
    }
    return nullptr;
}
int64_t MainWindow::chartTime(const History* first, const History* second) const {
    if (!paused_) return snapshot_.now;
    int64_t latest{};
    for (auto series : {first, second}) if (series && !series->points.empty()) latest = std::max(latest, series->points.back().time);
    return latest;
}
void MainWindow::updatePerformance() {
    if (!resourceTitle_) return;
    std::vector<std::wstring> keys;
    for (const auto& resource : snapshot_.resources) { keys.push_back(resource.id); resources_[resource.id] = resource; }
    std::set<std::wstring> keep(keys.begin(), keys.end());
    for (auto it = resources_.begin(); it != resources_.end();) {
        if (!keep.contains(it->first)) it = resources_.erase(it); else ++it;
    }
    reconcile(resourceList_, resourceItems_, resourceObjects_, keys);
    if (!resources_.contains(selectedResource_) && !keys.empty()) {
        selectedResource_ = keys.front(); resourceList_.SelectedItem(resourceObjects_.at(selectedResource_));
    }
    refreshContainers(resourceContainers_, [this](auto& key, auto& grid) { renderResource(key, grid); });
    const auto found = resources_.find(selectedResource_); if (found == resources_.end()) return;
    const auto& resource = found->second;
    const auto* first = history(resource.id); const auto* second = resource.kind == NKS_RESOURCE_NETWORK ? history(resource.id, 1) : nullptr;
    const auto now = chartTime(first, second);
    double maximum = resource.kind == NKS_RESOURCE_MEMORY ? std::max(1.0, resource.capacity) : 100;
    if (resource.kind == NKS_RESOURCE_NETWORK) {
        const auto start = now - static_cast<int64_t>(range_) * 1000000000;
        maximum = std::max(first ? first->maximum(start, 1e6) : 1e6, second ? second->maximum(start, 1e6) : 1e6);
    }
    const bool stale = !paused_ && resource.measured > 0 && snapshot_.now - resource.measured > 3000000000LL;
    text(resourceTitle_, resource.title); text(resourceDescription_, resource.description);
    text(resourceValue_, resourceValue(resource, stale)); resourceValue_.Foreground(brush(resourceColor(resource.kind)));
    const auto width = chartBox_.ActualWidth(); const auto height = chartBox_.ActualHeight();
    primary_.Stroke(brush(resourceColor(resource.kind))); secondary_.Stroke(brush(0x80c5c0));
    draw(primary_, first, width, height, now, range_, maximum);
    draw(secondary_, second, width, height, now, range_, maximum);
    for (uint32_t i = 0; i < 3; ++i) chartBox_.Children().GetAt(i).as<shapes::Line>().X2(width);
    size_t retained = first ? first->points.size() : 0;
    std::wstring facts = resource.status + L"\nRozsah " + std::to_wstring(range_) + L" s · historie " + std::to_wstring(retained)
        + L" vzorků · mezery nejsou spojeny čarou\n";
    if (resource.kind == NKS_RESOURCE_NETWORK) facts += L"Příjem a odesílání v Mbit/s";
    else if (resource.kind == NKS_RESOURCE_MEMORY) facts += L"Využitá fyzická paměť v GiB";
    else facts += L"Vytížení v %";
    if (resource.kind == NKS_RESOURCE_MEMORY)
        facts += L"\nDostupná " + metric(resource.secondary, L" GiB", 1073741824) + L" · potvrzená " + metric(resource.tertiary, L" GiB", 1073741824);
    if (resource.kind == NKS_RESOURCE_DISK)
        facts += L"\nČtení " + metric(resource.secondary, L" MiB/s", 1048576) + L" · zápis " + metric(resource.tertiary, L" MiB/s", 1048576);
    if (resource.kind == NKS_RESOURCE_GPU)
        facts += L"\nVyhrazená paměť " + metric(resource.secondary, L" GiB", 1073741824) + L" · sdílená " + metric(resource.tertiary, L" GiB", 1073741824);
    facts += L"\nStav: " + sampleState(resource.primary.state) + L" · zdroj " + std::to_wstring(resource.primary.source);
    if (!first) facts += L"\nHistorie zatím nedostupná";
    text(resourceFacts_, facts);
}
void MainWindow::renderResource(const std::wstring& key, controls::Grid const& grid) {
    auto found = resources_.find(key); if (found == resources_.end()) return;
    const auto& resource = found->second;
    if (grid.Children().Size() == 0) {
        column(grid, 66); column(grid); row(grid, 27); row(grid, 27);
        controls::Grid mini; mini.Width(50); mini.Height(31); controls::Grid::SetRowSpan(mini, 2);
        for (int i = 0; i < 2; ++i) { shapes::Path path; path.StrokeThickness(1.3); mini.Children().Append(path); }
        place(grid, mini, 0);
        place(grid, label(L"", 12), 0, 1); place(grid, label(L"", 10, 0x95a4ad), 1, 1);
    }
    const auto* first = history(resource.id); const auto* second = resource.kind == NKS_RESOURCE_NETWORK ? history(resource.id, 1) : nullptr;
    const auto now = chartTime(first, second);
    double maximum = resource.kind == NKS_RESOURCE_MEMORY ? std::max(1.0, resource.capacity) : 100;
    if (resource.kind == NKS_RESOURCE_NETWORK)
        maximum = std::max(first ? first->maximum(now - 60000000000LL, 1e6) : 1e6, second ? second->maximum(now - 60000000000LL, 1e6) : 1e6);
    auto mini = grid.Children().GetAt(0).as<controls::Grid>();
    auto primary = mini.Children().GetAt(0).as<shapes::Path>(); primary.Stroke(brush(resourceColor(resource.kind)));
    auto secondary = mini.Children().GetAt(1).as<shapes::Path>(); secondary.Stroke(brush(0x80c5c0));
    draw(primary, first, 50, 31, now, 60, maximum); draw(secondary, second, 50, 31, now, 60, maximum);
    text(grid.Children().GetAt(1).as<controls::TextBlock>(), resource.title);
    text(grid.Children().GetAt(2).as<controls::TextBlock>(), resourceValue(resource,
        !paused_ && resource.measured > 0 && snapshot_.now - resource.measured > 3000000000LL));
    xaml::Automation::AutomationProperties::SetName(grid, resource.title + L", " + resourceValue(resource, false));
}
}
