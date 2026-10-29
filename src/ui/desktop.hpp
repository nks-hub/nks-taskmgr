#pragma once

#include "core/history.hpp"
#include "core/monitor.hpp"
#include "core/resource.hpp"
#include "process_table.hpp"
#include "performance_layout.hpp"
#include "process_layout.hpp"
#include "service_table.hpp"
#include "service_layout.hpp"
#include "core/service_monitor.hpp"
#include <d2d1.h>
#include <dwrite.h>
#include <dwrite_1.h>
#include <wrl/client.h>

namespace nks {

enum class DesktopView { processes, performance, services };

class Desktop {
public:
    int run(HINSTANCE instance, int show, bool smoke);
    LRESULT message(UINT message, WPARAM wparam, LPARAM lparam);
    static LRESULT CALLBACK procedure(HWND window, UINT message, WPARAM wparam, LPARAM lparam);
private:
    void create();
    void setFonts();
    void layout();
    void tick();
    void changeView(DesktopView value);
    void paint(HDC destination = nullptr);
    void paintPerformance(float width, float height);
    void paintProcesses(float width, float height);
    void paintSearch(float x, float y, float width, bool shortcut);
    void createServices();
    void layoutServices();
    void tickServices();
    void selectService();
    void updateServiceDetail();
    void serviceCommand(unsigned id);
    void paintServices(float width, float height);
    void drawServiceCounter(const DRAWITEMSTRUCT& item);
    void drawDependency(const DRAWITEMSTRUCT& item);
    LRESULT drawServiceRow(const NMLVCUSTOMDRAW& item);
    LRESULT drawServiceHeader(const NMCUSTOMDRAW& item);
    std::optional<ProcessIdentity> serviceHost() const;
    LRESULT drawProcessRow(const NMLVCUSTOMDRAW& item);
    LRESULT drawProcessHeader(const NMCUSTOMDRAW& item);
    void drawOverview(const DRAWITEMSTRUCT& item);
    void processIcon(const std::wstring& name, float x, float y, float size);
    void drawResource(const DRAWITEMSTRUCT& item);
    void drawButton(const DRAWITEMSTRUCT& item);
    void drawConsumer(const DRAWITEMSTRUCT& item);
    void updateConsumers();
    DWRITE_TEXT_METRICS text(const std::wstring& value, D2D1_RECT_F box, float size, D2D1_COLOR_F color,
        int weight = 0, float spacing = 0, DWRITE_TEXT_ALIGNMENT alignment = DWRITE_TEXT_ALIGNMENT_LEADING);
    bool beginControl(const DRAWITEMSTRUCT& item, D2D1_COLOR_F background);
    void endControl();
    void icon(unsigned kind, float x, float y, float size, D2D1_COLOR_F color);
    void drawPolyline(std::span<const D2D1_POINT_2F> points, float width);
    void fill(D2D1_RECT_F box, D2D1_COLOR_F color, float radius = 0);
    void graph(const HistorySeries* series, D2D1_RECT_F box, D2D1_COLOR_F color, double maximum, bool grid, bool area = true);
    void rebuildResources();
    void scrollDetail(float position);
    PerformanceLayout performanceLayout() const;
    const ResourceRecord* selectedResource() const;
    HWND window{}, resourceList{}, search{}, processButton{}, performanceButton{}, pauseButton{}, intervalButton{};
    HWND servicesButton{}, serviceBody{}, serviceDependencies{}, serviceHostButton{};
    HMODULE richEditModule{};
    std::array<HWND, 4> serviceFilters{}, serviceCounters{};
    std::array<HWND, 2> serviceTabs{};
    HWND hoveredControl{};
    int hoveredResource{-1};
    bool focusFromKeyboard{};
    HWND minimizeButton{}, maximizeButton{}, closeButton{}, cpuTotalButton{}, cpuEachButton{}, cpuOverlayButton{}, range60Button{}, range600Button{}, allProcessesButton{};
    std::array<HWND, 3> processFilters{};
    std::array<HWND, 4> overviewButtons{};
    std::array<HWND, 3> consumerButtons{};
    std::array<std::optional<ProcessIdentity>, 3> consumers{};
    HFONT font{}, searchFont{};
    float scale{1};
    DesktopView view{DesktopView::performance};
    bool smokeMode{};
    bool processStale{}, resourceStale{};
    int ticks{}, smokeChecks{};
    int cpuView{}, rangeSeconds{60};
    float detailScroll{}, detailDragY{}, detailDragStart{};
    bool detailDragging{};
    std::wstring selectedId{L"cpu"}, processError, resourceError;
    ProcessTable table;
    std::shared_ptr<const ProcessSnapshot> processes;
    std::shared_ptr<const ResourceSnapshot> hardware;
    std::vector<ResourceRecord> resources;
    ResourceHistory history;
    Monitor monitor;
    ServiceTable serviceTable;
    ServiceMonitor serviceMonitor;
    std::shared_ptr<const ServiceSnapshot> services;
    std::shared_ptr<const ServiceDetail> serviceDetail;
    std::wstring selectedService, serviceError, serviceDetailError, processQuery, serviceQuery, serviceBodyText;
    std::vector<std::pair<std::wstring, bool>> dependencyRows;
    int serviceTab{};
    Microsoft::WRL::ComPtr<ID2D1Factory> factory;
    Microsoft::WRL::ComPtr<IDWriteFactory> writing;
    Microsoft::WRL::ComPtr<IDWriteRenderingParams> textRendering;
    Microsoft::WRL::ComPtr<ID2D1DCRenderTarget> target;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> brush;
    Microsoft::WRL::ComPtr<ID2D1DCRenderTarget> controlTarget;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> controlBrush;
    Microsoft::WRL::ComPtr<ID2D1StrokeStyle> roundedStroke;
    ID2D1RenderTarget* surface{};
    ID2D1SolidColorBrush* drawBrush{};
    ID2D1RenderTarget* previousSurface{};
    ID2D1SolidColorBrush* previousBrush{};
    std::map<std::pair<float, int>, Microsoft::WRL::ComPtr<IDWriteTextFormat>> formats;
    std::map<std::pair<float, int>, Microsoft::WRL::ComPtr<IDWriteInlineObject>> ellipses;
    std::map<HWND, D2D1_RECT_F> controlBounds;
    D2D1_SIZE_F controlSize{};
};

std::wstring decimal(double value, const wchar_t* unit, int digits = 1);
std::wstring resourceValue(const ResourceRecord& item);
D2D1_COLOR_F resourceColor(ResourceKind kind);

}
