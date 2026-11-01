#include "composition_renderer.hpp"

namespace nks {
using Microsoft::WRL::ComPtr;

CompositionRenderer::~CompositionRenderer() { release(); }

const wchar_t* CompositionRenderer::description() const noexcept
{
    if (!context_) return L"Direct2D · DC";
    return warp_ ? L"DirectComposition · WARP" : software_ ? L"DirectComposition · software" : L"DirectComposition · D3D11";
}
void CompositionRenderer::release()
{
    if (drawing_ && context_) context_->EndDraw();
    drawing_ = false;
    if (target_) target_->SetRoot(nullptr);
    if (composition_) composition_->Commit();
    if (context_) context_->SetTarget(nullptr);
    brush_.Reset(); bitmap_.Reset(); visual_.Reset(); target_.Reset(); composition_.Reset();
    context_.Reset(); d2d_.Reset(); swapChain_.Reset();
    if (d3dContext_) { d3dContext_->ClearState(); d3dContext_->Flush(); }
    d3dContext_.Reset(); d3d_.Reset();
    window_ = nullptr; pixels_ = {}; software_ = warp_ = false;
}
void CompositionRenderer::reset()
{
    release(); retryAt_ = 0; lastError_ = S_OK;
}
void CompositionRenderer::fail(HRESULT error)
{
    release(); lastError_ = error;
    retryAt_ = error == E_NOINTERFACE || error == E_NOTIMPL || error == DXGI_ERROR_UNSUPPORTED
        || error == DXGI_ERROR_INVALID_CALL ? ~ULONGLONG{} : GetTickCount64() + 5000;
}
HRESULT CompositionRenderer::bitmap()
{
    ComPtr<IDXGISurface> backBuffer;
    HRESULT result = swapChain_->GetBuffer(0, IID_PPV_ARGS(&backBuffer));
    if (FAILED(result)) return result;
    // The shared canvas is opaque, matching the DC path and its ClearType text.
    const auto properties = D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_IGNORE), 96, 96);
    result = context_->CreateBitmapFromDxgiSurface(backBuffer.Get(), &properties, &bitmap_);
    if (SUCCEEDED(result)) context_->SetTarget(bitmap_.Get());
    return result;
}
HRESULT CompositionRenderer::resize(D2D1_SIZE_U pixels)
{
    if (pixels.width == pixels_.width && pixels.height == pixels_.height) return S_OK;
    context_->SetTarget(nullptr); bitmap_.Reset();
    d3dContext_->ClearState(); d3dContext_->Flush();
    HRESULT result = swapChain_->ResizeBuffers(2, pixels.width, pixels.height, DXGI_FORMAT_B8G8R8A8_UNORM, 0);
    if (FAILED(result)) return result;
    result = bitmap();
    if (SUCCEEDED(result)) pixels_ = pixels;
    return result;
}
HRESULT CompositionRenderer::create(HWND window, ID2D1Factory* factory, D2D1_SIZE_U pixels, D3D_DRIVER_TYPE driver)
{
    ComPtr<ID2D1Factory1> modern;
    HRESULT result = factory->QueryInterface(IID_PPV_ARGS(&modern));
    if (FAILED(result)) return result;
    result = D3D11CreateDevice(nullptr, driver, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0,
        D3D11_SDK_VERSION, &d3d_, nullptr, &d3dContext_);
    if (FAILED(result)) return result;
    ComPtr<IDXGIDevice> dxgi; ComPtr<IDXGIAdapter> adapter; ComPtr<IDXGIFactory2> display;
    if (FAILED(result = d3d_.As(&dxgi)) || FAILED(result = dxgi->GetAdapter(&adapter))
        || FAILED(result = adapter->GetParent(IID_PPV_ARGS(&display)))
        || FAILED(result = modern->CreateDevice(dxgi.Get(), &d2d_))
        || FAILED(result = d2d_->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &context_))) return result;
    ComPtr<IDXGIAdapter1> adapter1; DXGI_ADAPTER_DESC1 description{};
    if (FAILED(result = adapter.As(&adapter1)) || FAILED(result = adapter1->GetDesc1(&description))) return result;
    software_ = (description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0; warp_ = driver == D3D_DRIVER_TYPE_WARP;
    DXGI_SWAP_CHAIN_DESC1 chain{};
    chain.Width = pixels.width; chain.Height = pixels.height; chain.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    chain.SampleDesc.Count = 1; chain.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; chain.BufferCount = 2;
    chain.Scaling = DXGI_SCALING_STRETCH; chain.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
    chain.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;
    if (FAILED(result = display->CreateSwapChainForComposition(d3d_.Get(), &chain, nullptr, &swapChain_))
        || FAILED(result = bitmap())
        || FAILED(result = context_->CreateSolidColorBrush(D2D1::ColorF(D2D1::ColorF::White), &brush_))
        || FAILED(result = DCompositionCreateDevice(dxgi.Get(), IID_PPV_ARGS(&composition_)))
        || FAILED(result = composition_->CreateTargetForHwnd(window, FALSE, &target_))
        || FAILED(result = composition_->CreateVisual(&visual_))
        || FAILED(result = visual_->SetContent(swapChain_.Get()))
        || FAILED(result = target_->SetRoot(visual_.Get()))) return result;
    context_->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_CLEARTYPE);
    window_ = window; pixels_ = pixels; return S_OK;
}
bool CompositionRenderer::begin(HWND window, ID2D1Factory* factory, D2D1_SIZE_U pixels, float dpi)
{
    if (!window || !factory || !pixels.width || !pixels.height || IsIconic(window)) return false;
    if (window_ && window_ != window) reset();
    if (!context_) {
        if (GetTickCount64() < retryAt_) return false;
        HRESULT result = create(window, factory, pixels, D3D_DRIVER_TYPE_HARDWARE);
        if (FAILED(result)) { release(); result = create(window, factory, pixels, D3D_DRIVER_TYPE_WARP); }
        if (FAILED(result)) { fail(result); return false; }
        lastError_ = S_OK;
    }
    BOOL valid = FALSE;
    HRESULT result = d3d_->GetDeviceRemovedReason();
    if (SUCCEEDED(result)) result = composition_->CheckDeviceState(&valid);
    if (FAILED(result) || !valid) { fail(FAILED(result) ? result : DXGI_ERROR_DEVICE_REMOVED); return false; }
    result = resize(pixels);
    if (FAILED(result)) { fail(result); return false; }
    context_->SetTarget(bitmap_.Get()); context_->SetDpi(dpi, dpi); context_->SetTransform(D2D1::Matrix3x2F::Identity());
    context_->BeginDraw(); drawing_ = true; return true;
}
CompositionRenderer::Presentation CompositionRenderer::end()
{
    if (!drawing_ || !context_) return Presentation::failed;
    drawing_ = false;
    HRESULT result = context_->EndDraw();
    if (FAILED(result)) { fail(result); return Presentation::failed; }
    DXGI_PRESENT_PARAMETERS update{};
    result = swapChain_->Present1(0, DXGI_PRESENT_DO_NOT_WAIT, &update);
    if (result == DXGI_ERROR_WAS_STILL_DRAWING) return Presentation::busy;
    if (result == DXGI_STATUS_OCCLUDED) return Presentation::occluded;
    if (FAILED(result)) { fail(result); return Presentation::failed; }
    result = composition_->Commit();
    if (FAILED(result)) { fail(result); return Presentation::failed; }
    return Presentation::presented;
}
}
