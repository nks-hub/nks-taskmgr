#pragma once

#include "core/history.hpp"
#include "core/resource.hpp"
#include "process_table.hpp"
#include "performance_layout.hpp"
#include <d2d1.h>
#include <dwrite.h>
#include <dwrite_1.h>
#include <wrl/client.h>
#include <atomic>
#include <condition_variable>
#include <mutex>
#include <thread>
#include <utility>

namespace nks {

template<class T>
struct SnapshotSlot {
    std::mutex mutex;
    std::shared_ptr<const T> latest;
    std::wstring error;
    void publish(T snapshot) {
        auto value = std::make_shared<const T>(std::move(snapshot));
        std::lock_guard lock(mutex);
        latest = std::move(value);
        error.clear();
    }
    void fail(const std::string& message) {
        std::lock_guard lock(mutex);
        error.assign(message.begin(), message.end());
    }
    std::shared_ptr<const T> take(std::wstring& failure) {
        std::lock_guard lock(mutex);
        failure = error;
        return std::exchange(latest, {});
    }
};

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
    void startCollection();
    void changeView(bool performance);
    void paint();
    void paintPerformance(float width, float height);
    void paintProcesses(float width, float height);
    void drawResource(const DRAWITEMSTRUCT& item);
    void drawButton(const DRAWITEMSTRUCT& item);
    void drawConsumer(const DRAWITEMSTRUCT& item);
    void updateConsumers();
    DWRITE_TEXT_METRICS text(const std::wstring& value, D2D1_RECT_F box, float size, D2D1_COLOR_F color,
        int weight = 0, float spacing = 0, DWRITE_TEXT_ALIGNMENT alignment = DWRITE_TEXT_ALIGNMENT_LEADING);
    bool beginControl(const DRAWITEMSTRUCT& item);
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
    HWND hoveredControl{};
    int hoveredResource{-1};
    bool focusFromKeyboard{};
    HWND minimizeButton{}, maximizeButton{}, closeButton{}, cpuTotalButton{}, cpuEachButton{}, cpuOverlayButton{}, range60Button{}, range600Button{}, allProcessesButton{};
    std::array<HWND, 3> consumerButtons{};
    std::array<std::optional<ProcessIdentity>, 3> consumers{};
    HFONT font{};
    float scale{1};
    bool performance{true}, smokeMode{};
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
    SnapshotSlot<ProcessSnapshot> processSlot;
    SnapshotSlot<ResourceSnapshot> resourceSlot;
    std::atomic<bool> paused{};
    std::atomic<unsigned> interval{1000};
    std::jthread processThread, resourceThread;
    std::condition_variable_any wake;
    std::mutex waitMutex;
    Microsoft::WRL::ComPtr<ID2D1Factory> factory;
    Microsoft::WRL::ComPtr<IDWriteFactory> writing;
    Microsoft::WRL::ComPtr<IDWriteRenderingParams> textRendering;
    Microsoft::WRL::ComPtr<ID2D1HwndRenderTarget> target;
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
