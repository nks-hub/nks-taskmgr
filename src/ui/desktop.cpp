#include "desktop.hpp"
#include <dwmapi.h>
#include <windowsx.h>
#include <uxtheme.h>
#include <algorithm>
#include <stdexcept>
#include <utility>
#include <cmath>

namespace nks {
namespace {
constexpr UINT_PTR refreshTimer = 1;
}

LRESULT CALLBACK Desktop::procedure(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    auto* self = reinterpret_cast<Desktop*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (msg == WM_NCCREATE) {
        self = static_cast<Desktop*>(reinterpret_cast<CREATESTRUCTW*>(lp)->lpCreateParams);
        self->window = hwnd;
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
    }
    return self ? self->message(msg, wp, lp) : DefWindowProcW(hwnd, msg, wp, lp);
}

int Desktop::run(HINSTANCE instance, int show, bool smoke)
{
    smokeMode = smoke;
    INITCOMMONCONTROLSEX controls{sizeof(controls), ICC_LISTVIEW_CLASSES};
    InitCommonControlsEx(&controls);
    if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, factory.GetAddressOf()))
        || FAILED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
            reinterpret_cast<IUnknown**>(writing.GetAddressOf())))) return 2;
    WNDCLASSEXW type{sizeof(type)};
    type.hInstance = instance;
    type.lpfnWndProc = procedure;
    type.lpszClassName = L"NksTaskManagerDesktop";
    type.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    type.hIcon = LoadIconW(instance, MAKEINTRESOURCEW(101));
    type.hIconSm = static_cast<HICON>(LoadImageW(instance, MAKEINTRESOURCEW(101), IMAGE_ICON,
        GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), LR_SHARED));
    const auto stroke = D2D1::StrokeStyleProperties(D2D1_CAP_STYLE_ROUND, D2D1_CAP_STYLE_ROUND,
        D2D1_CAP_STYLE_ROUND, D2D1_LINE_JOIN_ROUND);
    factory->CreateStrokeStyle(stroke, nullptr, 0, &roundedStroke);
    if (!RegisterClassExW(&type)) return 2;
    const auto dpi = GetDpiForSystem();
    if (!CreateWindowExW(0, type.lpszClassName, L"NKS Task Manager", WS_POPUP | WS_THICKFRAME | WS_SYSMENU
        | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_CLIPCHILDREN,
        CW_USEDEFAULT, CW_USEDEFAULT, MulDiv(1440, dpi, 96), MulDiv(920, dpi, 96), nullptr, nullptr, instance, this)) return 2;
    ShowWindow(window, show);
    UpdateWindow(window);
    MSG event{};
    while (GetMessageW(&event, nullptr, 0, 0) > 0) {
        if (view == DesktopView::performance && event.message == WM_MOUSEWHEEL) {
            POINT pointer{GET_X_LPARAM(event.lParam), GET_Y_LPARAM(event.lParam)}; ScreenToClient(window, &pointer);
            const auto frame = performanceLayout();
            if (pointer.x / scale >= frame.detailX && pointer.y / scale >= frame.top) {
                message(event.message, event.wParam, event.lParam); continue;
            }
        }
        if (event.message == WM_MOUSEMOVE && GetParent(event.hwnd) == window) {
            if (view == DesktopView::processes && event.hwnd == table.handle()) {
                LVHITTESTINFO hit{}; hit.pt = {GET_X_LPARAM(event.lParam), GET_Y_LPARAM(event.lParam)};
                const auto held = table.holding();
                table.setHeld(ListView_HitTest(table.handle(), &hit) >= 0);
                if (held != table.holding()) InvalidateRect(window, nullptr, FALSE);
            }
            const auto row = event.hwnd == resourceList ? static_cast<int>(LOWORD(SendMessageW(resourceList, LB_ITEMFROMPOINT, 0, event.lParam))) : -1;
            if (hoveredControl != event.hwnd || hoveredResource != row) {
                if (hoveredControl) InvalidateRect(hoveredControl, nullptr, FALSE);
                hoveredControl = event.hwnd; hoveredResource = row;
                TRACKMOUSEEVENT tracking{sizeof(tracking), TME_LEAVE, event.hwnd, 0}; TrackMouseEvent(&tracking);
                InvalidateRect(event.hwnd, nullptr, FALSE);
            }
        } else if (event.message == WM_MOUSELEAVE && event.hwnd == hoveredControl) {
            if (event.hwnd == table.handle()) { table.setHeld(false); InvalidateRect(window, nullptr, FALSE); }
            InvalidateRect(hoveredControl, nullptr, FALSE); hoveredControl = nullptr; hoveredResource = -1;
        }
        if ((event.message == WM_LBUTTONDOWN && focusFromKeyboard) || (event.message == WM_KEYDOWN && !focusFromKeyboard)) {
            focusFromKeyboard = event.message == WM_KEYDOWN;
            RedrawWindow(window, nullptr, nullptr, RDW_INVALIDATE | RDW_ALLCHILDREN);
        }
        if (event.message == WM_KEYDOWN && event.wParam == 'F' && (GetKeyState(VK_CONTROL) & 0x8000)) {
            if (view != DesktopView::services) changeView(DesktopView::processes);
            SetFocus(search); continue;
        }
        if (!IsDialogMessageW(window, &event)) { TranslateMessage(&event); DispatchMessageW(&event); }
    }
    monitor.stop();
    serviceMonitor.stop();
    if (richEditModule) FreeLibrary(richEditModule);
    if (font) DeleteObject(font);
    if (searchFont) DeleteObject(searchFont);
    return smokeMode ? (smokeChecks == 63 ? 0 : 3) : static_cast<int>(event.wParam);
}

void Desktop::create()
{
    scale = GetDpiForWindow(window) / 96.f;
    setFonts();
    const auto button = [&](const wchar_t* label, int id) {
        auto handle = CreateWindowExW(0, L"BUTTON", label, WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW,
            0, 0, 0, 0, window, reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)), nullptr, nullptr);
        SendMessageW(handle, WM_SETFONT, reinterpret_cast<WPARAM>(font), FALSE);
        return handle;
    };
    processButton = button(L"Procesy", 1);
    performanceButton = button(L"Výkon", 2);
    pauseButton = button(L"Pozastavit", 3);
    intervalButton = button(L"1 s", 4);
    minimizeButton = button(L"−", 10);
    maximizeButton = button(L"□", 11);
    closeButton = button(L"×", 12);
    cpuTotalButton = button(L"Celkem", 20);
    cpuEachButton = button(L"Logické", 21);
    cpuOverlayButton = button(L"Překrytí", 22);
    range60Button = button(L"60 s", 30);
    range600Button = button(L"10 min", 31);
    virtualNetworksButton = button(L"Virtuální síť: vypnuto", 32);
    allProcessesButton = button(L"Všechny procesy  →", 40);
    for (int i = 0; i < 3; ++i) consumerButtons[i] = button(L"Proces", 60 + i);
    const std::array filters{L"Všechny procesy", L"Aktivní", L"Strom"};
    for (int i = 0; i < 3; ++i) processFilters[i] = button(filters[i], 70 + i);
    const std::array overview{L"CPU", L"Paměť", L"Disky", L"Síť"};
    for (int i = 0; i < 4; ++i) overviewButtons[i] = button(overview[i], 80 + i);
    resourceList = CreateWindowExW(0, L"LISTBOX", L"Prostředky systému", WS_CHILD | WS_VISIBLE | WS_TABSTOP
        | WS_VSCROLL | LBS_NOTIFY | LBS_OWNERDRAWVARIABLE | LBS_HASSTRINGS | LBS_NOINTEGRALHEIGHT,
        0, 0, 0, 0, window, reinterpret_cast<HMENU>(5), nullptr, nullptr);
    SetWindowTheme(resourceList, L"DarkMode_Explorer", L"Scrollbar");
    search = CreateWindowExW(0, L"EDIT", L"", WS_CHILD | WS_TABSTOP | ES_AUTOHSCROLL,
        0, 0, 0, 0, window, reinterpret_cast<HMENU>(6), nullptr, nullptr);
    SendMessageW(search, WM_SETFONT, reinterpret_cast<WPARAM>(searchFont), FALSE);
    SendMessageW(search, EM_SETCUEBANNER, TRUE, reinterpret_cast<LPARAM>(L"Hledat název nebo PID"));
    table.create(window, font);
    createServices();
    BOOL dark = TRUE;
    DwmSetWindowAttribute(window, DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, sizeof(dark));
    SetTimer(window, refreshTimer, 100, nullptr);
    monitor.start();
    layout();
}

void Desktop::setFonts()
{
    const auto previousFont = font;
    const auto previousSearchFont = searchFont;
    Microsoft::WRL::ComPtr<IDWriteFontCollection> systemFonts;
    UINT32 familyIndex{}; BOOL available{};
    if (SUCCEEDED(writing->GetSystemFontCollection(&systemFonts))
        && SUCCEEDED(systemFonts->FindFamilyName(L"Segoe UI Variable", &familyIndex, &available)) && available) fontFamily = L"Segoe UI Variable";
    font = CreateFontW(-static_cast<int>(13 * scale), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, fontFamily.c_str());
    searchFont = CreateFontW(-static_cast<int>(13 * scale), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, fontFamily.c_str());
    Microsoft::WRL::ComPtr<IDWriteRenderingParams> rendering;
    if (SUCCEEDED(writing->CreateMonitorRenderingParams(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST), &rendering))) {
        textRendering = rendering;
        if (target) target->SetTextRenderingParams(textRendering.Get());
        if (controlTarget) controlTarget->SetTextRenderingParams(textRendering.Get());
    }
    for (const auto child : {processButton, performanceButton, pauseButton, intervalButton, virtualNetworksButton})
        if (child) SendMessageW(child, WM_SETFONT, reinterpret_cast<WPARAM>(font), FALSE);
    if (search) SendMessageW(search, WM_SETFONT, reinterpret_cast<WPARAM>(searchFont), FALSE);
    if (table.handle()) table.setFont(font);
    if (serviceTable.handle()) serviceTable.setFont(font);
    if (serviceBody) SendMessageW(serviceBody, WM_SETFONT, reinterpret_cast<WPARAM>(searchFont), FALSE);
    if (serviceDependencies) SendMessageW(serviceDependencies, LB_SETITEMHEIGHT, 0, std::lround(44 * scale));
    if (resourceList) {
        const auto count = SendMessageW(resourceList, LB_GETCOUNT, 0, 0);
        for (LRESULT i = 0; i < count; ++i) {
            const auto height = std::lround((i + 1) * 59 * scale) - std::lround(i * 59 * scale);
            SendMessageW(resourceList, LB_SETITEMHEIGHT, i, height);
        }
    }
    if (previousFont) DeleteObject(previousFont);
    if (previousSearchFont) DeleteObject(previousSearchFont);
}

void Desktop::changeView(DesktopView value)
{
    table.setHeld(false);
    view = value;
    serviceMonitor.setActive(view == DesktopView::services && !IsIconic(window));
    ShowWindow(resourceList, view == DesktopView::performance ? SW_SHOW : SW_HIDE);
    ShowWindow(virtualNetworksButton, view == DesktopView::performance ? SW_SHOW : SW_HIDE);
    table.setVisible(view == DesktopView::processes);
    serviceTable.setVisible(view == DesktopView::services);
    SetWindowTextW(search, (view == DesktopView::services ? serviceQuery : processQuery).c_str());
    SendMessageW(search, EM_SETCUEBANNER, TRUE, reinterpret_cast<LPARAM>(view == DesktopView::services
        ? L"Hledat službu, účet nebo PID" : L"Hledat název nebo PID"));
    ShowWindow(search, view == DesktopView::performance ? SW_HIDE : SW_SHOW);
    layout();
    RedrawWindow(window, nullptr, nullptr, RDW_INVALIDATE | RDW_ALLCHILDREN);
}

void Desktop::layout()
{
    RECT client{}; GetClientRect(window, &client);
    const auto width = client.right / scale, height = client.bottom / scale;
    const auto move = [&](HWND child, float x, float y, float w, float h) {
        controlBounds[child] = D2D1::RectF(x, y, x + w, y + h);
        const auto left = static_cast<int>(std::floor(x * scale)), top = static_cast<int>(std::floor(y * scale));
        MoveWindow(child, left, top, static_cast<int>(std::ceil((x + w) * scale)) - left,
            static_cast<int>(std::ceil((y + h) * scale)) - top, TRUE);
    };
    move(minimizeButton, width - 123, 0, 41, 48);
    move(maximizeButton, width - 82, 0, 41, 48);
    move(closeButton, width - 41, 0, 41, 48);
    const auto frame = performanceLayout();
    detailScroll = std::clamp(detailScroll, 0.f, frame.maxScroll);
    const auto navX = width <= 1000 ? 5.f : 8.f;
    const auto navWidth = frame.sidebar - navX * 2 - 1;
    move(processButton, navX, 127, navWidth, 66);
    move(performanceButton, navX, 197, navWidth, 66);
    move(servicesButton, navX, 267, navWidth, 66);
    const auto headerPadding = width <= 1000 ? 22.f : 30.f;
    move(intervalButton, width - headerPadding - 170, 84, 65, 35);
    move(pauseButton, width - headerPadding - 96, 84, 96, 35);
    ShowWindow(intervalButton, view == DesktopView::services ? SW_HIDE : SW_SHOW);
    move(virtualNetworksButton, frame.inventoryX + 10, 202, frame.inventoryWidth - 20, 30);
    move(resourceList, frame.inventoryX + 10, 240, frame.inventoryWidth - 20,
        std::max(30.f, std::min(height - 328, static_cast<float>(resources.size()) * 59)));
    const auto page = ProcessLayout::calculate(width, height);
    move(search, page.searchX + 34, page.searchY + 9, page.searchWidth - (width > 1000 ? 77 : 45), 19);
    move(table.handle(), page.left + 1, page.tableTop + 1, page.width - 2, page.tableHeight - 1);
    table.resizeColumns();
    const std::array filterX{4.f, 119.f, 189.f};
    const std::array filterWidths{112.f, 67.f, 61.f};
    for (std::size_t i = 0; i < processFilters.size(); ++i) {
        move(processFilters[i], page.left + filterX[i], page.toolbarTop + 4, filterWidths[i], 29);
        ShowWindow(processFilters[i], view == DesktopView::processes ? SW_SHOW : SW_HIDE);
    }
    const auto captionWidth = page.compact ? 0.f : (page.width - 46 - 100) / 5.3f * 1.3f;
    const auto gap = page.compact ? 13.f : width <= 1250 ? 17.f : 25.f;
    const auto overviewStart = page.left + 23 + (page.compact ? 0 : captionWidth + gap);
    const auto overviewWidth = (page.left + page.width - 23 - overviewStart - gap * 3) / 4;
    for (std::size_t i = 0; i < overviewButtons.size(); ++i) {
        move(overviewButtons[i], overviewStart + static_cast<float>(i) * (overviewWidth + gap), page.overviewTop + 20, overviewWidth, page.overviewHeight - 40);
        ShowWindow(overviewButtons[i], view == DesktopView::processes && page.overview ? SW_SHOW : SW_HIDE);
    }
    const auto scrolled = [&](HWND child, float x, float y, float w, float h, bool visible) {
        const auto shiftedY = y - detailScroll;
        move(child, x, shiftedY, w, h);
        const bool inside = shiftedY >= frame.top && shiftedY + h <= frame.bottom;
        ShowWindow(child, visible && view == DesktopView::performance && inside ? SW_SHOW : SW_HIDE);
    };
    scrolled(range60Button, frame.detailX + frame.chartWidth - 135, 183, 46, 27, true);
    scrolled(range600Button, frame.detailX + frame.chartWidth - 85, 183, 59, 27, true);
    const auto* selected = selectedResource();
    const auto cpu = selected && selected->kind == ResourceKind::cpu;
    scrolled(cpuTotalButton, frame.detailX + 22, 302, 64, 25, cpu);
    scrolled(cpuEachButton, frame.detailX + 87, 302, 64, 25, cpu);
    scrolled(cpuOverlayButton, frame.detailX + 152, 302, 64, 25, cpu);
    scrolled(allProcessesButton, frame.detailX + frame.available - 165, frame.rowY - 46, 165, 30, selected && (cpu || selected->kind == ResourceKind::memory));
    const auto consumerWidth = (frame.available - frame.consumerGap * 2) / 3;
    for (std::size_t i = 0; i < consumerButtons.size(); ++i)
        scrolled(consumerButtons[i], frame.detailX + static_cast<float>(i) * (consumerWidth + frame.consumerGap),
            frame.rowY, consumerWidth, 74, consumers[i].has_value());
    layoutServices();
    InvalidateRect(window, nullptr, FALSE);
}

void Desktop::tick()
{
    tickServices();
    bool changed = false;
    if (auto sample = monitor.takeProcesses(processError)) {
        const auto changes = processChanges(processes.get(), *sample);
        processes = std::move(sample);
        table.update(processes, changes);
        changed = true;
    }
    if (auto sample = monitor.takeResources(resourceError)) {
        hardware = std::move(sample);
        changed = true;
    }
    changed = readHistory() || changed;
    const auto now = std::chrono::steady_clock::now();
    const auto staleProcesses = !monitor.paused() && processes && now - processes->measuredAt > std::chrono::seconds(3);
    const auto staleResources = !monitor.paused() && hardware && now - hardware->measuredAt > std::chrono::seconds(3);
    if (processStale != staleProcesses || resourceStale != staleResources) {
        processStale = staleProcesses;
        resourceStale = staleResources;
        table.setStale(processStale);
        changed = true;
    }
    if (changed) rebuildResources();
    if (!IsIconic(window) && (changed || ticks % 10 == 0)) {
        InvalidateRect(window, nullptr, FALSE);
        InvalidateRect(resourceList, nullptr, FALSE);
    }
    if (smokeMode) {
        if (ticks == 40 && processes && !resources.empty()) {
            smokeChecks |= 1;
            changeView(DesktopView::processes);
            const auto match = std::find_if(processes->processes.begin(), processes->processes.end(), [](const auto& item) { return item.identity.pid == GetCurrentProcessId(); });
            if (match != processes->processes.end()) table.select(match->identity);
        }
        if (ticks == 60) {
            if (const auto* selection = table.selected(); selection && selection->identity.pid == GetCurrentProcessId()) smokeChecks |= 2;
            SetWindowTextW(search, std::to_wstring(GetCurrentProcessId()).c_str());
            if (const auto* selection = table.selected(); selection && selection->identity.pid == GetCurrentProcessId() && table.count() == 1) smokeChecks |= 4;
            SetWindowTextW(search, L"");
            selectedId = L"memory";
            rebuildResources();
            changeView(DesktopView::performance);
        }
        if (ticks == 70) {
            monitor.stop();
        }
        if (ticks == 80) changeView(DesktopView::services);
        if (ticks == 95 && services && !services->services.empty()) {
            smokeChecks |= 16;
            serviceTable.select(services->services.front().name); selectService();
        }
        if (ticks == 110 && serviceDetail && serviceDetail->name == selectedService) {
            smokeChecks |= 32; changeView(DesktopView::performance);
        }
        if (ticks == 115 && processStale && resourceStale) {
            const auto stale = std::all_of(resources.begin(), resources.end(), [](const auto& item) { return item.primary.state == SampleState::stale; });
            if (stale) smokeChecks |= 8;
        }
        if (ticks >= 120) DestroyWindow(window);
    }
    ++ticks;
}

void Desktop::rebuildResources()
{
    auto next = processes ? systemResources(*processes) : std::vector<ResourceRecord>{};
    if (processStale) for (auto& item : next)
        item.primary.state = item.secondary.state = item.tertiary.state = SampleState::stale;
    if (hardware) {
        next.insert(next.end(), hardware->devices.begin(), hardware->devices.end());
        if (resourceStale)
            for (auto& item : next)
                if (item.kind != ResourceKind::cpu && item.kind != ResourceKind::memory)
                    item.primary.state = item.secondary.state = item.tertiary.state = SampleState::stale;
    }
    if (!showVirtualNetworks) std::erase_if(next, [](const auto& item) {
        return item.kind == ResourceKind::network && std::any_of(item.facts.begin(), item.facts.end(), [](const auto& fact) {
            return fact.name == L"Druh rozhraní" && fact.value == L"Virtuální";
        });
    });
    bool same = next.size() == resources.size();
    if (same) for (std::size_t i = 0; i < next.size(); ++i) if (next[i].id != resources[i].id) same = false;
    resources = std::move(next);
    updateConsumers();
    if (!same) {
        SendMessageW(resourceList, LB_RESETCONTENT, 0, 0);
        for (const auto& item : resources) {
            const auto label = item.title + L", " + item.description;
            SendMessageW(resourceList, LB_ADDSTRING, 0, reinterpret_cast<LPARAM>(label.c_str()));
        }
    }
    if (!selectedResource() && !resources.empty()) selectedId = resources.front().id;
    for (std::size_t i = 0; i < resources.size(); ++i)
        if (resources[i].id == selectedId) SendMessageW(resourceList, LB_SETCURSEL, i, 0);
    layout();
}

const ResourceRecord* Desktop::selectedResource() const
{
    const auto found = std::find_if(resources.begin(), resources.end(), [&](const auto& item) { return item.id == selectedId; });
    return found == resources.end() ? nullptr : &*found;
}

PerformanceLayout Desktop::performanceLayout() const
{
    RECT bounds{}; GetClientRect(window, &bounds);
    const auto* resource = selectedResource();
    return PerformanceLayout::calculate(bounds.right / scale, bounds.bottom / scale, resource ? resource->kind : ResourceKind::cpu, resource ? resource->engines.size() : 0);
}

void Desktop::scrollDetail(float position)
{
    detailScroll = std::clamp(position, 0.f, performanceLayout().maxScroll);
    layout();
}

LRESULT Desktop::message(UINT msg, WPARAM wp, LPARAM lp)
{
    switch (msg) {
    case WM_CREATE: create(); return 0;
    case WM_NCCALCSIZE: if (wp) return 0; break;
    case WM_SIZE: serviceMonitor.setActive(view == DesktopView::services && !IsIconic(window)); layout(); return 0;
    case WM_DPICHANGED: {
        scale = HIWORD(wp) / 96.f;
        setFonts();
        const auto* rect = reinterpret_cast<RECT*>(lp);
        if (target) target->SetDpi(HIWORD(wp), HIWORD(wp));
        SetWindowPos(window, nullptr, rect->left, rect->top, rect->right - rect->left, rect->bottom - rect->top, SWP_NOZORDER);
        return 0;
    }
    case WM_GETMINMAXINFO: {
        auto* bounds = reinterpret_cast<MINMAXINFO*>(lp);
        bounds->ptMinTrackSize = {static_cast<LONG>(720 * scale), static_cast<LONG>(500 * scale)};
        MONITORINFO monitorInfo{sizeof(monitorInfo)};
        if (GetMonitorInfoW(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST), &monitorInfo)) {
            bounds->ptMaxPosition = {monitorInfo.rcWork.left - monitorInfo.rcMonitor.left, monitorInfo.rcWork.top - monitorInfo.rcMonitor.top};
            bounds->ptMaxSize = {monitorInfo.rcWork.right - monitorInfo.rcWork.left, monitorInfo.rcWork.bottom - monitorInfo.rcWork.top};
        }
        return 0;
    }
    case WM_NCHITTEST: {
        const auto native = DefWindowProcW(window, msg, wp, lp);
        if (native != HTCLIENT) return native;
        POINT point{GET_X_LPARAM(lp), GET_Y_LPARAM(lp)}; ScreenToClient(window, &point);
        if (!IsZoomed(window)) {
            RECT bounds{}; GetClientRect(window, &bounds);
            const auto border = static_cast<LONG>(8 * scale);
            const bool left = point.x < border, right = point.x >= bounds.right - border;
            const bool top = point.y < border, bottom = point.y >= bounds.bottom - border;
            if (top && left) return HTTOPLEFT;
            if (top && right) return HTTOPRIGHT;
            if (bottom && left) return HTBOTTOMLEFT;
            if (bottom && right) return HTBOTTOMRIGHT;
            if (left) return HTLEFT;
            if (right) return HTRIGHT;
            if (top) return HTTOP;
            if (bottom) return HTBOTTOM;
        }
        if (point.y < 48 * scale) return HTCAPTION;
        return HTCLIENT;
    }
    case WM_TIMER: if (wp == refreshTimer) tick(); return 0;
    case WM_ERASEBKGND: return 1;
    case WM_VKEYTOITEM:
        if (reinterpret_cast<HWND>(lp) == serviceDependencies && LOWORD(wp) == VK_RETURN) { serviceCommand(103); return -2; }
        break;
    case WM_MOUSEWHEEL:
        if (view == DesktopView::performance) { scrollDetail(detailScroll - GET_WHEEL_DELTA_WPARAM(wp) / static_cast<float>(WHEEL_DELTA) * 48); return 0; }
        break;
    case WM_LBUTTONDOWN:
        if (view == DesktopView::performance) {
            const auto frame = performanceLayout();
            const auto x = GET_X_LPARAM(lp) / scale, y = GET_Y_LPARAM(lp) / scale;
            if (frame.maxScroll > 0 && x >= frame.detailRight - 17 && x <= frame.detailRight && y >= frame.top && y <= frame.bottom) {
                detailDragging = true; detailDragY = y; detailDragStart = detailScroll; SetCapture(window); return 0;
            }
        }
        break;
    case WM_MOUSEMOVE:
        if (detailDragging) {
            const auto frame = performanceLayout();
            const auto viewport = frame.bottom - frame.top;
            const auto thumb = std::max(30.f, viewport * viewport / frame.contentHeight);
            scrollDetail(detailDragStart + (GET_Y_LPARAM(lp) / scale - detailDragY) / std::max(1.f, viewport - thumb) * frame.maxScroll);
            return 0;
        }
        break;
    case WM_LBUTTONUP:
        if (detailDragging) { detailDragging = false; ReleaseCapture(); return 0; }
        break;
    case WM_CAPTURECHANGED: detailDragging = false; break;
    case WM_PAINT: paint(); return 0;
    case WM_PRINTCLIENT: paint(reinterpret_cast<HDC>(wp)); return 0;
    case WM_DRAWITEM: {
        const auto& item = *reinterpret_cast<DRAWITEMSTRUCT*>(lp);
        if (item.CtlID == 103) drawDependency(item);
        else if (item.CtlType == ODT_LISTBOX) drawResource(item);
        else if (item.CtlID >= 120 && item.CtlID <= 123) drawServiceCounter(item);
        else if (item.CtlID >= 60 && item.CtlID <= 62) drawConsumer(item);
        else if (item.CtlID >= 80 && item.CtlID <= 83) drawOverview(item);
        else drawButton(item);
        return TRUE;
    }
    case WM_MEASUREITEM: {
        auto& item = *reinterpret_cast<MEASUREITEMSTRUCT*>(lp);
        if (item.CtlID == 103) { item.itemHeight = static_cast<UINT>(44 * scale); return TRUE; }
        if (item.CtlType == ODT_LISTBOX && item.CtlID == 5) {
            const auto index = item.itemID < resources.size() ? item.itemID : 0;
            item.itemHeight = static_cast<UINT>(std::lround((index + 1) * 59 * scale) - std::lround(index * 59 * scale));
            return TRUE;
        }
        break;
    }
    case WM_CTLCOLORLISTBOX:
        SetBkColor(reinterpret_cast<HDC>(wp), RGB(29, 37, 42));
        SetDCBrushColor(reinterpret_cast<HDC>(wp), RGB(29, 37, 42));
        return reinterpret_cast<LRESULT>(GetStockObject(DC_BRUSH));
    case WM_CTLCOLOREDIT:
        SetTextColor(reinterpret_cast<HDC>(wp), RGB(231, 240, 244));
        SetBkColor(reinterpret_cast<HDC>(wp), RGB(18, 24, 28));
        SetDCBrushColor(reinterpret_cast<HDC>(wp), RGB(18, 24, 28));
        return reinterpret_cast<LRESULT>(GetStockObject(DC_BRUSH));
    case WM_COMMAND:
        if (LOWORD(wp) == 7 || (LOWORD(wp) >= 103 && LOWORD(wp) <= 132)) {
            if (LOWORD(wp) != 103 || HIWORD(wp) == LBN_DBLCLK) serviceCommand(LOWORD(wp));
            return 0;
        }
        switch (LOWORD(wp)) {
        case 1: changeView(DesktopView::processes); break;
        case 2: changeView(DesktopView::performance); break;
        case 3: monitor.setPaused(!monitor.paused()); serviceMonitor.setPaused(monitor.paused()); SetWindowTextW(pauseButton, monitor.paused() ? L"Pokračovat" : L"Pozastavit"); InvalidateRect(window, nullptr, FALSE); break;
        case 4: monitor.setInterval(monitor.interval() == 1000 ? 250 : 1000); SetWindowTextW(intervalButton, monitor.interval() == 1000 ? L"1 s" : L"250 ms"); break;
        case 10: ShowWindow(window, SW_MINIMIZE); break;
        case 11: ShowWindow(window, IsZoomed(window) ? SW_RESTORE : SW_MAXIMIZE); break;
        case 12: DestroyWindow(window); break;
        case 20: case 21: case 22:
            cpuView = LOWORD(wp) - 20; InvalidateRect(window, nullptr, FALSE);
            for (auto control : {cpuTotalButton, cpuEachButton, cpuOverlayButton}) InvalidateRect(control, nullptr, FALSE);
            break;
        case 30: case 31:
            rangeSeconds = LOWORD(wp) == 30 ? 60 : 600; InvalidateRect(window, nullptr, FALSE);
            InvalidateRect(range60Button, nullptr, FALSE); InvalidateRect(range600Button, nullptr, FALSE); break;
        case 32:
            showVirtualNetworks = !showVirtualNetworks;
            SetWindowTextW(virtualNetworksButton, showVirtualNetworks ? L"Virtuální síť: zapnuto" : L"Virtuální síť: vypnuto");
            rebuildResources(); InvalidateRect(virtualNetworksButton, nullptr, FALSE); InvalidateRect(window, nullptr, FALSE); break;
        case 40: changeView(DesktopView::processes); break;
        case 70: case 71: case 72:
            table.setMode(static_cast<ProcessMode>(LOWORD(wp) - 70));
            for (auto filter : processFilters) InvalidateRect(filter, nullptr, FALSE);
            InvalidateRect(window, nullptr, FALSE); break;
        case 80: case 81: case 82: case 83: {
            const std::array kinds{ResourceKind::cpu, ResourceKind::memory, ResourceKind::disk, ResourceKind::network};
            const auto kind = kinds[LOWORD(wp) - 80];
            auto match = std::find_if(resources.begin(), resources.end(), [&](const auto& item) { return item.kind == kind && item.primary.valid(); });
            if (match == resources.end()) match = std::find_if(resources.begin(), resources.end(), [&](const auto& item) { return item.kind == kind; });
            if (match != resources.end()) { selectedId = match->id; detailScroll = 0; rebuildResources(); changeView(DesktopView::performance); }
            break;
        }
        case 60: case 61: case 62: {
            const auto index = LOWORD(wp) - 60;
            if (consumers[index]) {
                SetWindowTextW(search, L""); table.setMode(ProcessMode::all);
                changeView(DesktopView::processes); table.select(*consumers[index]); SetFocus(table.handle());
            }
            break;
        }
        case 5: if (HIWORD(wp) == LBN_SELCHANGE) {
            const auto index = SendMessageW(resourceList, LB_GETCURSEL, 0, 0);
            if (index >= 0 && static_cast<std::size_t>(index) < resources.size()) {
                if (selectedId != resources[index].id) detailScroll = 0;
                selectedId = resources[index].id;
            }
            updateConsumers(); layout();
            InvalidateRect(window, nullptr, FALSE);
        } break;
        case 6: if (HIWORD(wp) == EN_CHANGE) {
            wchar_t value[512]{}; GetWindowTextW(search, value, 512);
            if (view == DesktopView::services) {
                serviceQuery = value; serviceTable.filter(serviceTable.filterMode(), value); selectService();
            } else { processQuery = value; table.filter(value); }
        } break;
        }
        return 0;
    case WM_NOTIFY:
        if (reinterpret_cast<NMHDR*>(lp)->hwndFrom == serviceTable.header() && reinterpret_cast<NMHDR*>(lp)->code == NM_CUSTOMDRAW)
            return drawServiceHeader(*reinterpret_cast<NMCUSTOMDRAW*>(lp));
        if (reinterpret_cast<NMHDR*>(lp)->hwndFrom == serviceTable.handle()) {
            const auto code = reinterpret_cast<NMHDR*>(lp)->code;
            if (code == NM_CUSTOMDRAW) return drawServiceRow(*reinterpret_cast<NMLVCUSTOMDRAW*>(lp));
            const auto result = serviceTable.notify(reinterpret_cast<NMHDR*>(lp));
            if (!serviceTable.updating() && (code == LVN_ITEMCHANGED || code == LVN_COLUMNCLICK)) selectService();
            return result;
        }
        if (reinterpret_cast<NMHDR*>(lp)->hwndFrom == table.header() && reinterpret_cast<NMHDR*>(lp)->code == NM_CUSTOMDRAW)
            return drawProcessHeader(*reinterpret_cast<NMCUSTOMDRAW*>(lp));
        if (reinterpret_cast<NMHDR*>(lp)->hwndFrom == table.handle()) {
            if (reinterpret_cast<NMHDR*>(lp)->code == NM_CUSTOMDRAW) return drawProcessRow(*reinterpret_cast<NMLVCUSTOMDRAW*>(lp));
            InvalidateRect(window, nullptr, FALSE);
            return table.notify(reinterpret_cast<NMHDR*>(lp));
        }
        break;
    case WM_CLOSE: DestroyWindow(window); return 0;
    case WM_DESTROY: KillTimer(window, refreshTimer); PostQuitMessage(0); return 0;
    }
    return DefWindowProcW(window, msg, wp, lp);
}

}
