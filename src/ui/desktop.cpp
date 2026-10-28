#include "desktop.hpp"
#include "collectors/process_collector.hpp"
#include "collectors/resource_collector.hpp"
#include "core/process_sampler.hpp"
#include <dwmapi.h>
#include <windowsx.h>
#include <algorithm>
#include <stdexcept>
#include <utility>
#include <cmath>

namespace nks {
namespace {
constexpr UINT_PTR refreshTimer = 1;
constexpr auto background = RGB(23, 31, 36);
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
    type.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
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
        if (performance && event.message == WM_MOUSEWHEEL) {
            POINT pointer{GET_X_LPARAM(event.lParam), GET_Y_LPARAM(event.lParam)}; ScreenToClient(window, &pointer);
            const auto frame = performanceLayout();
            if (pointer.x / scale >= frame.detailX && pointer.y / scale >= frame.top) {
                message(event.message, event.wParam, event.lParam); continue;
            }
        }
        if (event.message == WM_MOUSEMOVE && GetParent(event.hwnd) == window) {
            const auto row = event.hwnd == resourceList ? static_cast<int>(LOWORD(SendMessageW(resourceList, LB_ITEMFROMPOINT, 0, event.lParam))) : -1;
            if (hoveredControl != event.hwnd || hoveredResource != row) {
                if (hoveredControl) InvalidateRect(hoveredControl, nullptr, FALSE);
                hoveredControl = event.hwnd; hoveredResource = row;
                TRACKMOUSEEVENT tracking{sizeof(tracking), TME_LEAVE, event.hwnd, 0}; TrackMouseEvent(&tracking);
                InvalidateRect(event.hwnd, nullptr, FALSE);
            }
        } else if (event.message == WM_MOUSELEAVE && event.hwnd == hoveredControl) {
            InvalidateRect(hoveredControl, nullptr, FALSE); hoveredControl = nullptr; hoveredResource = -1;
        }
        if ((event.message == WM_LBUTTONDOWN && focusFromKeyboard) || (event.message == WM_KEYDOWN && !focusFromKeyboard)) {
            focusFromKeyboard = event.message == WM_KEYDOWN;
            RedrawWindow(window, nullptr, nullptr, RDW_INVALIDATE | RDW_ALLCHILDREN);
        }
        if (event.message == WM_KEYDOWN && event.wParam == 'F' && (GetKeyState(VK_CONTROL) & 0x8000)) {
            changeView(false); SetFocus(search); continue;
        }
        if (!IsDialogMessageW(window, &event)) { TranslateMessage(&event); DispatchMessageW(&event); }
    }
    processThread.request_stop();
    resourceThread.request_stop();
    wake.notify_all();
    processThread.join();
    resourceThread.join();
    if (font) DeleteObject(font);
    return smokeMode ? (smokeChecks == 15 ? 0 : 3) : static_cast<int>(event.wParam);
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
    allProcessesButton = button(L"Všechny procesy  →", 40);
    for (int i = 0; i < 3; ++i) consumerButtons[i] = button(L"Proces", 60 + i);
    resourceList = CreateWindowExW(0, L"LISTBOX", L"Prostředky systému", WS_CHILD | WS_VISIBLE | WS_TABSTOP
        | WS_VSCROLL | LBS_NOTIFY | LBS_OWNERDRAWVARIABLE | LBS_HASSTRINGS | LBS_NOINTEGRALHEIGHT,
        0, 0, 0, 0, window, reinterpret_cast<HMENU>(5), nullptr, nullptr);
    search = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"", WS_CHILD | WS_TABSTOP | ES_AUTOHSCROLL,
        0, 0, 0, 0, window, reinterpret_cast<HMENU>(6), nullptr, nullptr);
    SendMessageW(search, WM_SETFONT, reinterpret_cast<WPARAM>(font), FALSE);
    SendMessageW(search, EM_SETCUEBANNER, TRUE, reinterpret_cast<LPARAM>(L"Hledat název nebo PID   Ctrl+F"));
    table.create(window, font);
    BOOL dark = TRUE;
    DwmSetWindowAttribute(window, DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, sizeof(dark));
    SetTimer(window, refreshTimer, 100, nullptr);
    startCollection();
    layout();
}

void Desktop::setFonts()
{
    const auto previousFont = font;
    font = CreateFontW(-static_cast<int>(13 * scale), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
    Microsoft::WRL::ComPtr<IDWriteRenderingParams> monitor;
    if (SUCCEEDED(writing->CreateMonitorRenderingParams(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST), &monitor))) {
        textRendering.Reset();
        // Match the HTML reference coverage while retaining monitor contrast and pixel geometry.
        writing->CreateCustomRenderingParams(3.6f, monitor->GetEnhancedContrast(), monitor->GetClearTypeLevel(),
            monitor->GetPixelGeometry(), monitor->GetRenderingMode(), &textRendering);
        if (target) target->SetTextRenderingParams(textRendering.Get());
        if (controlTarget) controlTarget->SetTextRenderingParams(textRendering.Get());
    }
    for (const auto child : {processButton, performanceButton, pauseButton, intervalButton, search})
        if (child) SendMessageW(child, WM_SETFONT, reinterpret_cast<WPARAM>(font), FALSE);
    if (table.handle()) table.setFont(font);
    if (resourceList) {
        const auto count = SendMessageW(resourceList, LB_GETCOUNT, 0, 0);
        for (LRESULT i = 0; i < count; ++i) {
            const auto height = std::lround((i + 1) * 59 * scale) - std::lround(i * 59 * scale);
            SendMessageW(resourceList, LB_SETITEMHEIGHT, i, height);
        }
    }
    if (previousFont) DeleteObject(previousFont);
}

void Desktop::startCollection()
{
    const auto wait = [&](std::stop_token stop) {
        std::unique_lock lock(waitMutex);
        wake.wait_for(lock, stop, std::chrono::milliseconds(interval.load()), [] { return false; });
    };
    processThread = std::jthread([this, wait](std::stop_token stop) {
        try {
            ProcessCollector collector;
            ProcessSampler sampler;
            while (!stop.stop_requested()) {
                if (!paused.load()) {
                    try { auto sample = collector.collect(); sampler.apply(sample); processSlot.publish(std::move(sample)); }
                    catch (const std::exception& error) { processSlot.fail(error.what()); }
                }
                wait(stop);
            }
        } catch (const std::exception& error) { processSlot.fail(error.what()); }
    });
    resourceThread = std::jthread([this, wait](std::stop_token stop) {
        try {
            ResourceCollector collector;
            while (!stop.stop_requested()) {
                if (!paused.load()) {
                    try { resourceSlot.publish(collector.collect()); }
                    catch (const std::exception& error) { resourceSlot.fail(error.what()); }
                }
                wait(stop);
            }
        } catch (const std::exception& error) { resourceSlot.fail(error.what()); }
    });
}

void Desktop::changeView(bool value)
{
    performance = value;
    ShowWindow(resourceList, value ? SW_SHOW : SW_HIDE);
    ShowWindow(table.handle(), value ? SW_HIDE : SW_SHOW);
    ShowWindow(search, value ? SW_HIDE : SW_SHOW);
    layout();
    InvalidateRect(window, nullptr, FALSE);
    InvalidateRect(processButton, nullptr, FALSE);
    InvalidateRect(performanceButton, nullptr, FALSE);
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
    const auto headerPadding = width <= 1000 ? 22.f : 30.f;
    move(intervalButton, width - headerPadding - 170, 84, 65, 35);
    move(pauseButton, width - headerPadding - 96, 84, 96, 35);
    move(resourceList, frame.inventoryX + 10, 202, frame.inventoryWidth - 20,
        std::max(30.f, std::min(height - 290, static_cast<float>(resources.size()) * 59)));
    const auto headerX = frame.sidebar + headerPadding;
    move(search, headerX, 151, std::max(100.f, width - headerX - headerPadding), 35);
    move(table.handle(), headerX, 205, std::max(100.f, width - headerX - headerPadding), std::max(60.f, height - 360));
    const auto scrolled = [&](HWND child, float x, float y, float w, float h, bool visible) {
        const auto shiftedY = y - detailScroll;
        move(child, x, shiftedY, w, h);
        const bool inside = shiftedY >= frame.top && shiftedY + h <= frame.bottom;
        ShowWindow(child, visible && performance && inside ? SW_SHOW : SW_HIDE);
    };
    scrolled(range60Button, frame.detailX + frame.chartWidth - 135, 183, 46, 27, true);
    scrolled(range600Button, frame.detailX + frame.chartWidth - 85, 183, 59, 27, true);
    const auto* selected = selectedResource();
    const auto cpu = selected && selected->kind == ResourceKind::cpu;
    scrolled(cpuTotalButton, frame.detailX + 22, 302, 64, 25, cpu);
    scrolled(cpuEachButton, frame.detailX + 87, 302, 64, 25, cpu);
    scrolled(cpuOverlayButton, frame.detailX + 152, 302, 64, 25, cpu);
    scrolled(allProcessesButton, frame.detailX + frame.available - 165, frame.rowY - 46, 165, 30, true);
    const auto consumerWidth = (frame.available - frame.consumerGap * 2) / 3;
    for (std::size_t i = 0; i < consumerButtons.size(); ++i)
        scrolled(consumerButtons[i], frame.detailX + static_cast<float>(i) * (consumerWidth + frame.consumerGap),
            frame.rowY, consumerWidth, 74, consumers[i].has_value());
    if (target) target->Resize(D2D1::SizeU(client.right, client.bottom));
    InvalidateRect(window, nullptr, FALSE);
}

void Desktop::tick()
{
    bool changed = false;
    if (auto sample = processSlot.take(processError)) {
        processes = std::move(sample);
        table.update(processes);
        for (const auto& item : systemResources(*processes)) history.append(item.id, {processes->measuredAt, item.primary});
        for (const auto& processor : processes->processors)
            history.append(L"cpu:" + std::to_wstring(processor.group) + L":" + std::to_wstring(processor.index), {processes->measuredAt, processor.cpuPercent});
        history.append(L"pagefile", {processes->measuredAt, {static_cast<double>(processes->memory.pageFileUsedBytes), processes->memory.pageFileState, MetricSource::performanceInfo}});
        changed = true;
    }
    if (auto sample = resourceSlot.take(resourceError)) {
        hardware = std::move(sample);
        for (const auto& item : hardware->devices) {
            history.append(item.id, {hardware->measuredAt, item.primary});
            if (item.kind == ResourceKind::network) history.append(item.id + L":up", {hardware->measuredAt, item.secondary});
        }
        changed = true;
    }
    const auto now = std::chrono::steady_clock::now();
    const auto staleProcesses = !paused.load() && processes && now - processes->measuredAt > std::chrono::seconds(3);
    const auto staleResources = !paused.load() && hardware && now - hardware->measuredAt > std::chrono::seconds(3);
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
            changeView(false);
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
            changeView(true);
        }
        if (ticks == 70) {
            processThread.request_stop(); resourceThread.request_stop(); wake.notify_all();
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
    return PerformanceLayout::calculate(bounds.right / scale, bounds.bottom / scale, resource ? resource->kind : ResourceKind::cpu);
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
    case WM_SIZE: layout(); return 0;
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
        MONITORINFO monitor{sizeof(monitor)};
        if (GetMonitorInfoW(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST), &monitor)) {
            bounds->ptMaxPosition = {monitor.rcWork.left - monitor.rcMonitor.left, monitor.rcWork.top - monitor.rcMonitor.top};
            bounds->ptMaxSize = {monitor.rcWork.right - monitor.rcWork.left, monitor.rcWork.bottom - monitor.rcWork.top};
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
    case WM_MOUSEWHEEL:
        if (performance) { scrollDetail(detailScroll - GET_WHEEL_DELTA_WPARAM(wp) / static_cast<float>(WHEEL_DELTA) * 48); return 0; }
        break;
    case WM_LBUTTONDOWN:
        if (performance) {
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
    case WM_DRAWITEM: {
        const auto& item = *reinterpret_cast<DRAWITEMSTRUCT*>(lp);
        if (item.CtlType == ODT_LISTBOX) drawResource(item);
        else if (item.CtlID >= 60 && item.CtlID <= 62) drawConsumer(item);
        else drawButton(item);
        return TRUE;
    }
    case WM_MEASUREITEM: {
        auto& item = *reinterpret_cast<MEASUREITEMSTRUCT*>(lp);
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
        SetBkColor(reinterpret_cast<HDC>(wp), background);
        SetDCBrushColor(reinterpret_cast<HDC>(wp), background);
        return reinterpret_cast<LRESULT>(GetStockObject(DC_BRUSH));
    case WM_COMMAND:
        switch (LOWORD(wp)) {
        case 1: changeView(false); break;
        case 2: changeView(true); break;
        case 3: paused.store(!paused.load()); SetWindowTextW(pauseButton, paused.load() ? L"Pokračovat" : L"Pozastavit"); InvalidateRect(window, nullptr, FALSE); break;
        case 4: interval.store(interval.load() == 1000 ? 250 : 1000); SetWindowTextW(intervalButton, interval.load() == 1000 ? L"1 s" : L"250 ms"); break;
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
        case 40: changeView(false); break;
        case 60: case 61: case 62: {
            const auto index = LOWORD(wp) - 60;
            if (consumers[index]) { SetWindowTextW(search, L""); changeView(false); table.select(*consumers[index]); SetFocus(table.handle()); }
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
            wchar_t value[512]{}; GetWindowTextW(search, value, 512); table.filter(value);
        } break;
        }
        return 0;
    case WM_NOTIFY:
        if (reinterpret_cast<NMHDR*>(lp)->hwndFrom == table.handle()) {
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
