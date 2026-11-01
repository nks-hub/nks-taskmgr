#include "window.hpp"
#include <sstream>

namespace nks::candidate {
namespace {
constexpr double widths[]{260, 70, 86, 120, 126, 108, 108};
constexpr const wchar_t* captions[]{L"Proces", L"PID", L"CPU", L"Pracovní sada", L"Privátní paměť", L"Čtení", L"Zápis"};
void processColumns(controls::Grid const& grid) { for (double width : widths) column(grid, width); }
int compareMetric(NksMetric a, NksMetric b) {
    if (valid(a) != valid(b)) return valid(a) ? -1 : 1;
    return a.value < b.value ? -1 : a.value > b.value ? 1 : 0;
}
template<typename T> int compare(T a, T b) { return a < b ? -1 : a > b ? 1 : 0; }
}
void MainWindow::buildProcesses() {
    row(processPage_); row(processPage_, 115);
    controls::Grid table; row(table, 39); row(table); table.MinWidth(1010);
    controls::Grid header; processColumns(header); header.Background(brush(0x1d252a));
    for (int i = 0; i < 7; ++i) {
        auto item = button(captions[i]); item.Height(38);
        item.HorizontalAlignment(xaml::HorizontalAlignment::Stretch);
        item.HorizontalContentAlignment(i ? xaml::HorizontalAlignment::Right : xaml::HorizontalAlignment::Left);
        item.Click([this, i](auto&&, auto&&) {
            descending_ = sortColumn_ == i ? !descending_ : i != 0;
            sortColumn_ = i; updateProcesses(true);
        });
        place(header, item, 0, i);
    }
    setupList(processList_, 33); processList_.SelectionMode(controls::ListViewSelectionMode::Extended);
    processList_.ItemsSource(processItems_);
    bindContainers(processList_, processContainers_, [this](auto& key, auto& grid) { renderProcess(key, grid); });
    processList_.SelectionChanged([this](auto&&, auto&&) { if (!updating_) selection(); });
    processList_.PointerMoved([this](auto&&, auto&&) { held_ = true; });
    processList_.PointerExited([this](auto&&, auto&&) { held_ = false; updateProcesses(true); });
    processList_.DoubleTapped([this](auto&&, auto&&) { toggleTree(); });
    processList_.KeyDown([this](auto&&, xaml::Input::KeyRoutedEventArgs const& args) {
        const auto key = token(processList_.SelectedItem());
        if (mode_.SelectedIndex() != 2 || !branches_.contains(key)) return;
        if (args.Key() == winrt::Windows::System::VirtualKey::Left && !collapsed_.contains(key)) {
            collapsed_.insert(key); updateProcesses(true); args.Handled(true);
        } else if (args.Key() == winrt::Windows::System::VirtualKey::Right && collapsed_.contains(key)) {
            collapsed_.erase(key); updateProcesses(true); args.Handled(true);
        }
    });
    place(table, header, 0); place(table, processList_, 1);
    controls::ScrollViewer horizontal;
    horizontal.HorizontalScrollBarVisibility(controls::ScrollBarVisibility::Auto);
    horizontal.VerticalScrollBarVisibility(controls::ScrollBarVisibility::Disabled);
    horizontal.Content(table); place(processPage_, horizontal, 0);
    processFacts_ = label(L"Vyberte proces. Ctrl/Shift umožňují více výběrů.", 11, 0x95a4ad);
    processFacts_.TextWrapping(xaml::TextWrapping::Wrap); processFacts_.IsTextSelectionEnabled(true);
    controls::ScrollViewer facts; facts.Content(processFacts_); place(processPage_, facts, 1);
    body_.Children().Append(processPage_);
}
std::vector<std::wstring> MainWindow::processOrder() {
    std::vector<std::wstring> order;
    for (const auto& [key, process] : processes_) {
        if (process.ended) continue;
        if (mode_.SelectedIndex() == 1 && !(valid(process.cpu) && process.cpu.value > 0)
            && !(valid(process.read) && process.read.value > 0) && !(valid(process.write) && process.write.value > 0)) continue;
        if (!query_.empty() && lower(process.name).find(query_) == std::wstring::npos
            && std::to_wstring(process.key.pid).find(query_) == std::wstring::npos) continue;
        order.push_back(key);
    }
    std::sort(order.begin(), order.end(), [this](const auto& a, const auto& b) {
        const auto& x = processes_.at(a); const auto& y = processes_.at(b);
        if (sortColumn_ == 2 || sortColumn_ == 5 || sortColumn_ == 6) {
            const auto mx = sortColumn_ == 2 ? x.cpu : sortColumn_ == 5 ? x.read : x.write;
            const auto my = sortColumn_ == 2 ? y.cpu : sortColumn_ == 5 ? y.read : y.write;
            if (valid(mx) != valid(my)) return valid(mx);
        }
        int result{};
        switch (sortColumn_) {
        case 0: result = compare(lower(x.name), lower(y.name)); break;
        case 1: result = compare(x.key.pid, y.key.pid); break;
        case 2: result = compareMetric(x.cpu, y.cpu); break;
        case 3: result = compare(x.workingSet, y.workingSet); break;
        case 4: result = compare(x.privateBytes, y.privateBytes); break;
        case 5: result = compareMetric(x.read, y.read); break;
        case 6: result = compareMetric(x.write, y.write); break;
        }
        return result ? (descending_ ? result > 0 : result < 0) : x.key < y.key;
    });
    depths_.clear(); branches_.clear();
    if (mode_.SelectedIndex() != 2 || !query_.empty()) return order;
    std::map<uint32_t, std::wstring> byPid;
    for (const auto& key : order) byPid[processes_.at(key).key.pid] = key;
    std::map<std::wstring, std::vector<std::wstring>> children;
    std::vector<std::wstring> roots;
    for (const auto& key : order) {
        const auto& child = processes_.at(key);
        auto found = byPid.find(child.parent);
        bool parentVerified = false;
        if (found != byPid.end() && found->second != key) {
            const auto& parent = processes_.at(found->second);
            parentVerified = (child.key.bootLow || child.key.bootHigh) && child.key.bootLow == parent.key.bootLow
                && child.key.bootHigh == parent.key.bootHigh && child.created && parent.created && parent.created <= child.created;
        }
        if (parentVerified) { children[found->second].push_back(key); branches_.insert(found->second); }
        else roots.push_back(key);
    }
    std::vector<std::wstring> result;
    std::set<std::wstring> visited;
    auto visit = [&](const std::wstring& root) {
        struct Entry { std::wstring key; int depth; bool visible; };
        std::vector<Entry> pending{{root, 0, true}};
        while (!pending.empty()) {
            auto current = std::move(pending.back()); pending.pop_back();
            if (!visited.insert(current.key).second) continue;
            depths_[current.key] = std::min(12, current.depth);
            if (current.visible) result.push_back(current.key);
            const auto& list = children[current.key];
            for (auto it = list.rbegin(); it != list.rend(); ++it)
                pending.push_back({*it, current.depth + 1, current.visible && !collapsed_.contains(current.key)});
        }
    };
    for (const auto& key : roots) visit(key);
    for (const auto& key : order) if (!visited.contains(key)) visit(key);
    return result;
}
void MainWindow::updateProcesses(bool force) {
    if (!snapshot_.processChanged && !force) {
        const bool stale = !paused_ && snapshot_.processTime > 0 && snapshot_.now - snapshot_.processTime > 3000000000LL;
        for (auto& [key, process] : processes_) { (void)key; process.stale = stale; }
    } else {
        std::set<std::wstring> live;
        for (const auto& process : snapshot_.processes) {
            const auto key = process.key.text(); live.insert(key); processes_[key] = process;
        }
        for (auto it = processes_.begin(); it != processes_.end();) {
            if (!live.contains(it->first)) {
                if (held_ && !force && processObjects_.contains(it->first)) { it->second.ended = true; ++it; }
                else it = processes_.erase(it);
            } else ++it;
        }
        if (!held_ || force) reconcile(processList_, processItems_, processObjects_, processOrder());
    }
    refreshContainers(processContainers_, [this](auto& key, auto& grid) { renderProcess(key, grid); });
    selection();
}
void MainWindow::toggleTree() {
    const auto key = token(processList_.SelectedItem());
    if (mode_.SelectedIndex() != 2 || !branches_.contains(key)) return;
    if (!collapsed_.erase(key)) collapsed_.insert(key);
    updateProcesses(true);
}
void MainWindow::renderProcess(const std::wstring& key, controls::Grid const& grid) {
    auto found = processes_.find(key); if (found == processes_.end()) return;
    const auto& process = found->second;
    if (grid.Children().Size() == 0) {
        processColumns(grid); grid.BorderBrush(brush(0x2c363d)); grid.BorderThickness({0, 0, 0, 1});
        for (int i = 0; i < 7; ++i) {
            auto block = label(L"", 13); block.Margin({i ? 0.0 : 16.0, 0, 12, 0});
            block.HorizontalAlignment(i ? xaml::HorizontalAlignment::Right : xaml::HorizontalAlignment::Stretch);
            place(grid, block, 0, i);
        }
    }
    const auto unavailable = process.ended ? L"Ukončen" : process.stale ? L"Zastaralá data" : L"";
    std::wstring name = process.name;
    if (mode_.SelectedIndex() == 2 && query_.empty()) {
        name = std::wstring(static_cast<size_t>(depths_[key]) * 2, L' ') +
            (branches_.contains(key) ? (collapsed_.contains(key) ? L"▸ " : L"▾ ") : L"  ") + name;
    }
    if (*unavailable) name += L" · " + std::wstring(unavailable);
    std::array<std::wstring, 7> values{name, std::to_wstring(process.key.pid),
        *unavailable ? unavailable : metric(process.cpu, L" %"),
        *unavailable ? unavailable : number(static_cast<double>(process.workingSet) / 1048576, L" MiB"),
        *unavailable ? unavailable : number(static_cast<double>(process.privateBytes) / 1048576, L" MiB"),
        *unavailable ? unavailable : metric(process.read, L" MiB/s", 1048576),
        *unavailable ? unavailable : metric(process.write, L" MiB/s", 1048576)};
    for (uint32_t i = 0; i < values.size(); ++i) text(grid.Children().GetAt(i).as<controls::TextBlock>(), values[i]);
    xaml::Automation::AutomationProperties::SetName(grid, name + L", PID " + std::to_wstring(process.key.pid));
}
void MainWindow::selection() {
    const auto key = token(processList_.SelectedItem());
    const auto found = processes_.find(key);
    if (found == processes_.end()) { text(processFacts_, L"Vyberte proces. Ctrl/Shift umožňují více výběrů."); return; }
    const auto& process = found->second;
    const auto verified = (process.key.bootLow || process.key.bootHigh) && process.key.kind && process.key.generation;
    text(processFacts_, process.name + L" · PID " + std::to_wstring(process.key.pid) + L" · vybráno " + std::to_wstring(processList_.SelectedItems().Size())
        + L"\n" + (verified ? L"Ověřená identita " : L"Neověřená identita ") + process.key.text()
        + L"\nVlákna " + std::to_wstring(process.threads) + L" · popisovače " + std::to_wstring(process.handles)
        + L" · rodičovský PID " + std::to_wstring(process.parent) + L" · čas vzniku "
        + (process.created ? std::to_wstring(process.created) + L" (100 ns FILETIME)" : L"nedostupný"));
}
}
