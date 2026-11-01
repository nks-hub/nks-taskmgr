#pragma once
#include <d2d1_1.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <dcomp.h>
#include <wrl/client.h>

namespace nks {
class CompositionRenderer {
public:
    enum class Presentation { presented, busy, occluded, failed };
    ~CompositionRenderer();
    bool begin(HWND window, ID2D1Factory* factory, D2D1_SIZE_U pixels, float dpi);
    Presentation end();
    void reset();
    ID2D1RenderTarget* surface() const noexcept { return context_.Get(); }
    ID2D1SolidColorBrush* brush() const noexcept { return brush_.Get(); }
    const wchar_t* description() const noexcept;
    HRESULT lastError() const noexcept { return lastError_; }
private:
    HRESULT create(HWND window, ID2D1Factory* factory, D2D1_SIZE_U pixels, D3D_DRIVER_TYPE driver);
    HRESULT bitmap();
    HRESULT resize(D2D1_SIZE_U pixels);
    void release();
    void fail(HRESULT error);
    Microsoft::WRL::ComPtr<ID3D11Device> d3d_;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> d3dContext_;
    Microsoft::WRL::ComPtr<ID2D1Device> d2d_;
    Microsoft::WRL::ComPtr<ID2D1DeviceContext> context_;
    Microsoft::WRL::ComPtr<IDXGISwapChain1> swapChain_;
    Microsoft::WRL::ComPtr<ID2D1Bitmap1> bitmap_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> brush_;
    Microsoft::WRL::ComPtr<IDCompositionDevice> composition_;
    Microsoft::WRL::ComPtr<IDCompositionTarget> target_;
    Microsoft::WRL::ComPtr<IDCompositionVisual> visual_;
    HWND window_{};
    D2D1_SIZE_U pixels_{};
    ULONGLONG retryAt_{};
    HRESULT lastError_{S_OK};
    bool drawing_{}, software_{}, warp_{};
};
}
