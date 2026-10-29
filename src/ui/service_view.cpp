#include "desktop.hpp"
#include <richedit.h>
#include <uxtheme.h>
#include <algorithm>
#include <cmath>
#include <stdexcept>

namespace nks {
namespace {
std::wstring failure(DWORD error)
{
    if (error == ERROR_ACCESS_DENIED) return L"Chybí oprávnění ke čtení";
    return L"Nedostupné (chyba " + std::to_wstring(error) + L")";
}
}

void Desktop::createServices()
{
    const auto button = [&](const wchar_t* label, int id) {
        return CreateWindowExW(0, L"BUTTON", label, WS_CHILD | WS_TABSTOP | BS_OWNERDRAW,
            0, 0, 0, 0, window, reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)), nullptr, nullptr);
    };
    servicesButton = button(L"Služby", 7); ShowWindow(servicesButton, SW_SHOW);
    const std::array filters{L"Všechny", L"Běžící", L"Zastavené", L"Ostatní"};
    for (int i = 0; i < 4; ++i) {
        serviceFilters[i] = button(filters[i], 110 + i);
        serviceCounters[i] = button(filters[i], 120 + i);
    }
    serviceTabs[0] = button(L"Přehled", 130); serviceTabs[1] = button(L"Závislosti", 131);
    serviceHostButton = button(L"Otevřít proces →", 132);
    richEditModule = LoadLibraryW(L"Msftedit.dll");
    if (!richEditModule) throw std::runtime_error("RichEdit could not be loaded");
    serviceBody = CreateWindowExW(0, MSFTEDIT_CLASS, L"", WS_CHILD | WS_TABSTOP | WS_VSCROLL | ES_MULTILINE | ES_READONLY,
        0, 0, 0, 0, window, reinterpret_cast<HMENU>(104), nullptr, nullptr);
    if (!serviceBody) throw std::runtime_error("Service detail could not be created");
    SetWindowTheme(serviceBody, L"DarkMode_Explorer", nullptr);
    SendMessageW(serviceBody, EM_SETBKGNDCOLOR, 0, RGB(23, 29, 33));
    SendMessageW(serviceBody, WM_SETFONT, reinterpret_cast<WPARAM>(searchFont), FALSE);
    CHARFORMAT2W format{sizeof(format)};
    format.dwMask = CFM_COLOR | CFM_FACE | CFM_SIZE;
    format.crTextColor = RGB(233, 238, 240); format.yHeight = 165;
    lstrcpyW(format.szFaceName, L"Segoe UI");
    SendMessageW(serviceBody, EM_SETCHARFORMAT, SCF_DEFAULT, reinterpret_cast<LPARAM>(&format));
    serviceDependencies = CreateWindowExW(0, L"LISTBOX", L"Závislosti služby", WS_CHILD | WS_TABSTOP | WS_VSCROLL
        | LBS_NOTIFY | LBS_OWNERDRAWFIXED | LBS_HASSTRINGS | LBS_NOINTEGRALHEIGHT | LBS_WANTKEYBOARDINPUT,
        0, 0, 0, 0, window, reinterpret_cast<HMENU>(103), nullptr, nullptr);
    SetWindowTheme(serviceDependencies, L"DarkMode_Explorer", nullptr);
    serviceTable.create(window, font);
    serviceMonitor.start();
}

void Desktop::layoutServices()
{
    RECT bounds{}; GetClientRect(window, &bounds);
    const auto frame = ServiceLayout::calculate(bounds.right / scale, bounds.bottom / scale);
    const bool visible = view == DesktopView::services;
    const auto move = [&](HWND child, float x, float y, float w, float h, bool show) {
        controlBounds[child] = D2D1::RectF(x, y, x + w, y + h);
        const auto left = static_cast<int>(std::floor(x * scale)), top = static_cast<int>(std::floor(y * scale));
        MoveWindow(child, left, top, static_cast<int>(std::ceil((x + w) * scale)) - left,
            static_cast<int>(std::ceil((y + h) * scale)) - top, TRUE);
        ShowWindow(child, visible && show ? SW_SHOW : SW_HIDE);
    };
    const auto counterWidth = std::min(176.f, frame.width / 4);
    for (unsigned i = 0; i < 4; ++i) {
        move(serviceCounters[i], frame.left + frame.width - counterWidth * (4 - i) - 1, frame.overviewTop + 18, counterWidth, 55, frame.overview);
        move(serviceFilters[i], frame.left + 23 + i * 76, frame.workspaceTop + 21, 74, 29, true);
    }
    move(serviceTable.handle(), frame.left + 1, frame.tableTop, frame.inventoryWidth - 2, frame.tableHeight - 1, true);
    serviceTable.resizeColumns();
    const auto detailTop = frame.stacked ? frame.tableTop + frame.tableHeight : frame.workspaceTop;
    const auto tabsTop = detailTop + (frame.stacked ? 68 : selectedService.empty() ? 88 : frame.compactDetail ? 134 : 224);
    move(serviceHostButton, frame.detailX + (frame.stacked ? frame.detailWidth - 164 : 23),
        detailTop + (frame.stacked ? 14 : frame.compactDetail ? 82 : 176), 141, 35, selectedService.size() > 0);
    EnableWindow(serviceHostButton, serviceHost().has_value());
    for (unsigned i = 0; i < 2; ++i) move(serviceTabs[i], frame.detailX + 23 + i * 105, tabsTop, 100, 27, true);
    const auto bodyTop = tabsTop + 45;
    const auto bodyHeight = std::max(30.f, frame.bottom - bodyTop - 14);
    const auto errorHeight = serviceTab == 1 && !dependencyRows.empty() && !serviceBodyText.empty() ? 54.f : 0.f;
    move(serviceBody, frame.detailX + 23, bodyTop, frame.detailWidth - 46, errorHeight > 0 ? errorHeight : bodyHeight,
        serviceTab == 0 || dependencyRows.empty() || errorHeight > 0);
    move(serviceDependencies, frame.detailX + 23, bodyTop + errorHeight, frame.detailWidth - 46, std::max(30.f, bodyHeight - errorHeight),
        serviceTab == 1 && !dependencyRows.empty());
}

void Desktop::tickServices()
{
    bool changed = false;
    if (auto snapshot = serviceMonitor.take(serviceError)) {
        services = std::move(snapshot); serviceTable.update(services); selectService(); changed = true;
    }
    if (auto detail = serviceMonitor.takeDetail(serviceDetailError); detail && detail->name == selectedService) {
        serviceDetail = std::move(detail); changed = true;
    }
    if (changed && view == DesktopView::services) {
        updateServiceDetail(); layoutServices();
        InvalidateRect(window, nullptr, FALSE);
        for (auto counter : serviceCounters) InvalidateRect(counter, nullptr, FALSE);
    }
}

void Desktop::selectService()
{
    const auto* selected = serviceTable.selected();
    const auto name = selected ? selected->name : std::wstring{};
    if (name == selectedService) return;
    selectedService = name; serviceDetail.reset(); serviceDetailError.clear(); serviceTab = 0;
    serviceMonitor.select(name);
    updateServiceDetail(); layoutServices();
    InvalidateRect(window, nullptr, FALSE);
    for (auto tab : serviceTabs) InvalidateRect(tab, nullptr, FALSE);
}

std::optional<ProcessIdentity> Desktop::serviceHost() const
{
    const auto* selected = serviceTable.selected();
    if (!selected || !processes || !serviceDetail || processStale || !services || serviceDetail->hostCreated100ns == 0
        || selected->name != serviceDetail->name || selected->state != SERVICE_RUNNING || selected->pid != serviceDetail->pid
        || std::chrono::steady_clock::now() - services->measuredAt > std::chrono::seconds(3)) return {};
    return serviceHostIdentity(*selected, *serviceDetail, *processes);
}

void Desktop::updateServiceDetail()
{
    std::wstring body;
    std::vector<std::pair<std::wstring, bool>> dependencies;
    if (selectedService.empty()) body = L"Vyberte službu ze seznamu.\r\n\r\nPodrobnosti lze označit a kopírovat.";
    else if (!serviceDetail) body = !serviceDetailError.empty() ? L"Detail nelze načíst: " + serviceDetailError
        : monitor.paused() ? L"Sběr je pozastavený." : L"Načítám podrobnosti služby…";
    else if (serviceTab == 1) {
        if (serviceDetail->config.error) body = L"Závislosti: " + failure(serviceDetail->config.error);
        else for (const auto& name : serviceDetail->config.dependencies) dependencies.emplace_back(name, false);
        if (serviceDetail->dependentsError) body += L"\r\nNavazující služby: " + failure(serviceDetail->dependentsError);
        else for (const auto& name : serviceDetail->dependents) dependencies.emplace_back(name, true);
        if (dependencies.empty() && body.empty()) body = L"Tato služba nemá závislosti ani navazující služby.";
    } else {
        const auto& detail = *serviceDetail;
        body = detail.descriptionError ? failure(detail.descriptionError) : detail.description.empty() ? L"Služba nemá popis." : detail.description;
        const auto field = [&](const wchar_t* label, const std::wstring& value) { body += L"\r\n\r\n" + std::wstring(label) + L"\r\n" + value; };
        field(L"Systémový název", detail.name);
        if (detail.config.error) field(L"Konfigurace", failure(detail.config.error));
        else {
            field(L"Účet", detail.config.account.empty() ? L"Neuvedeno" : detail.config.account);
            field(L"Způsob spouštění", serviceStartup(detail.config));
            field(L"Skupina", detail.config.group.empty() ? L"Bez skupiny" : detail.config.group);
            field(L"Spouštěcí příkaz", detail.config.command);
        }
        if (detail.statusError) field(L"Hostitelský proces", failure(detail.statusError));
        else if (!detail.pid) field(L"Hostitelský proces", L"Neběží");
        else {
            std::wstring host = L"PID " + std::to_wstring(detail.pid);
            if (services) {
                const auto count = std::count_if(services->services.begin(), services->services.end(), [&](const auto& item) { return item.pid == detail.pid; });
                if (count > 1) host += L" · " + std::to_wstring(count) + L" služeb ve společném procesu";
            }
            if (!serviceHost()) host += L"\r\n" + (detail.hostError ? failure(detail.hostError) : L"Identitu procesu nyní nelze ověřit.");
            field(L"Hostitelský proces", host);
        }
    }
    if (dependencyRows != dependencies) {
        dependencyRows = std::move(dependencies);
        SendMessageW(serviceDependencies, LB_RESETCONTENT, 0, 0);
        for (const auto& [name, dependent] : dependencyRows) {
            const auto label = name + (dependent ? L" · Navazující služba" : name.starts_with(L"+") ? L" · Skupina" : L" · Potřebuje ke spuštění");
            SendMessageW(serviceDependencies, LB_ADDSTRING, 0, reinterpret_cast<LPARAM>(label.c_str()));
        }
    }
    if (body != serviceBodyText) {
        serviceBodyText = std::move(body);
        auto richText = serviceBodyText; std::erase(richText, L'\n');
        SetWindowTextW(serviceBody, richText.c_str());
        const auto formatRange = [&](LONG begin, LONG end, LONG size, COLORREF color) {
            CHARRANGE range{begin, end}; SendMessageW(serviceBody, EM_EXSETSEL, 0, reinterpret_cast<LPARAM>(&range));
            CHARFORMAT2W format{sizeof(format)}; format.dwMask = CFM_COLOR | CFM_SIZE;
            format.crTextColor = color; format.yHeight = size;
            SendMessageW(serviceBody, EM_SETCHARFORMAT, SCF_SELECTION, reinterpret_cast<LPARAM>(&format));
        };
        formatRange(0, -1, 165, RGB(233, 238, 240));
        PARAFORMAT2 paragraph{sizeof(paragraph)};
        paragraph.dwMask = PFM_LINESPACING; paragraph.bLineSpacingRule = 5; paragraph.dyLineSpacing = 28;
        SendMessageW(serviceBody, EM_SETPARAFORMAT, 0, reinterpret_cast<LPARAM>(&paragraph));
        const auto descriptionEnd = richText.find(L"\r\r");
        formatRange(0, descriptionEnd == std::wstring::npos ? -1 : static_cast<LONG>(descriptionEnd), 165, RGB(149, 164, 173));
        if (serviceTab == 0) for (const auto* label : {L"Systémový název", L"Účet", L"Způsob spouštění", L"Skupina", L"Spouštěcí příkaz", L"Hostitelský proces", L"Konfigurace"}) {
            const auto marker = L"\r\r" + std::wstring(label) + L"\r";
            const auto found = richText.find(marker);
            if (found != std::wstring::npos) formatRange(static_cast<LONG>(found + 2), static_cast<LONG>(found + marker.size() - 1), 135, RGB(149, 164, 173));
        }
        CHARRANGE start{0, 0}; SendMessageW(serviceBody, EM_EXSETSEL, 0, reinterpret_cast<LPARAM>(&start));
        SendMessageW(serviceBody, EM_SCROLL, SB_TOP, 0);
    }
}

void Desktop::serviceCommand(unsigned id)
{
    if (id == 7) { changeView(DesktopView::services); return; }
    if ((id >= 110 && id <= 113) || (id >= 120 && id <= 123)) {
        serviceTable.filter(static_cast<ServiceFilter>(id % 10), serviceQuery); selectService();
        for (auto filter : serviceFilters) InvalidateRect(filter, nullptr, FALSE);
        for (auto counter : serviceCounters) InvalidateRect(counter, nullptr, FALSE);
    } else if (id == 130 || id == 131) {
        serviceTab = static_cast<int>(id) - 130; updateServiceDetail(); layoutServices();
        for (auto tab : serviceTabs) InvalidateRect(tab, nullptr, FALSE);
    } else if (id == 132) {
        if (auto host = serviceHost()) {
            processQuery.clear(); table.setMode(ProcessMode::all); changeView(DesktopView::processes);
            table.select(*host); ListView_EnsureVisible(table.handle(), ListView_GetNextItem(table.handle(), -1, LVNI_SELECTED), FALSE);
            SetFocus(table.handle());
        }
    } else if (id == 103) {
        const auto row = SendMessageW(serviceDependencies, LB_GETCURSEL, 0, 0);
        if (row >= 0 && static_cast<std::size_t>(row) < dependencyRows.size() && services) {
            const auto& name = dependencyRows[row].first;
            if (std::any_of(services->services.begin(), services->services.end(), [&](const auto& item) { return serviceNameEqual(item.name, name); })) {
                serviceQuery.clear(); SetWindowTextW(search, L""); serviceTable.filter(ServiceFilter::all, L"");
                serviceTable.select(name); selectService();
                ListView_EnsureVisible(serviceTable.handle(), ListView_GetNextItem(serviceTable.handle(), -1, LVNI_SELECTED), FALSE);
                SetFocus(serviceTable.handle());
            }
        }
    }
    InvalidateRect(window, nullptr, FALSE);
}

}
