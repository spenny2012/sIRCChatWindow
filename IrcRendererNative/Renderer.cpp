#include "pch.h"
#include "Renderer.h"
#include "IrcParser.h"
#include "Palette.h"
#include "TextCells.h"

#include <cmath>

namespace
{
    constexpr float LineHeightRatio = 1.25f;
    // Per-frame drain cap: at 60 fps this absorbs ~61k lines/s of flood while
    // costing well under a millisecond (ingest is parse + wrap arithmetic only).
    constexpr int   MaxInputBatch = 1024;

    inline D2D1_COLOR_F ColorFromU32(uint32_t c)
    {
        return D2D1_COLOR_F{
            static_cast<float>((c >> 16) & 0xFF) / 255.0f,
            static_cast<float>((c >> 8) & 0xFF) / 255.0f,
            static_cast<float>(c & 0xFF) / 255.0f,
            static_cast<float>((c >> 24) & 0xFF) / 255.0f
        };
    }

    inline D2D1_RECT_F Rect(float x, float y, float w, float h)
    {
        D2D1_RECT_F r = { x, y, x + w, y + h };
        return r;
    }

    constexpr float LeftPadDips = 4.0f;
    constexpr float RightPadDips = 4.0f;
    constexpr int   ContinuationIndentChars = 2;

    const wchar_t* ChildClassName = L"IrcChatSurface";

    LRESULT CALLBACK ChildWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
        case WM_NCHITTEST:
            // Same-thread pass-through: the WPF window behind this child gets
            // all mouse input, so selection/wheel wiring stays in managed code.
            return HTTRANSPARENT;
        case WM_PAINT:
            // The swapchain owns the pixels; just validate.
            ValidateRect(hwnd, nullptr);
            return 0;
        case WM_ERASEBKGND:
            return 1;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    HMODULE ThisModule()
    {
        HMODULE module = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
            GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&ChildWndProc), &module);
        return module;
    }

    bool EnsureChildClass()
    {
        static bool registered = false;
        if (registered)
            return true;

        WNDCLASSW wc = {};
        wc.lpfnWndProc = ChildWndProc;
        wc.hInstance = ThisModule();
        wc.lpszClassName = ChildClassName;
        registered = RegisterClassW(&wc) != 0;
        return registered;
    }

    // Greedy word wrap over the visible text of one line. Returns the number of
    // rows; if rowStarts is non-null it receives rows+1 offsets (rowStarts[r] is
    // where row r begins, rowStarts[rows] == length), so row r renders the range
    // [rowStarts[r], rowStarts[r+1]). Used both when committing a line (count
    // only) and when rendering it (with rowStarts) — they must never disagree.
    // asciiOnly (the stored per-line flag) selects the byte==cell fast path;
    // otherwise the budget is in cells and rows only break at cluster starts.
    uint16_t ComputeWrapRows(const char* text, uint16_t length, bool asciiOnly,
        int cols, int contCols, uint16_t* rowStarts, uint16_t maxRows)
    {
        if (cols < 1) cols = 1;
        if (contCols < 1) contCols = 1;
        if (maxRows < 1) maxRows = 1;

        uint16_t rows = 0;
        uint16_t pos = 0;

        if (asciiOnly)
        {
            for (;;)
            {
                if (rowStarts)
                    rowStarts[rows] = pos;

                const int budget = (rows == 0) ? cols : contCols;
                const int remaining = static_cast<int>(length) - static_cast<int>(pos);
                ++rows;

                if (remaining <= budget || rows >= maxRows)
                    break;

                // Break after the last space that fits. A space landing exactly on
                // the boundary hangs invisibly off the row end (i == budget + 1), so
                // the full row of text is kept. Hard-break mid-word only when no
                // space fits at all.
                uint16_t rowLen = static_cast<uint16_t>(budget);
                for (int i = budget + 1; i >= 1; --i)
                {
                    if (text[pos + i - 1] == ' ')
                    {
                        rowLen = static_cast<uint16_t>(i);
                        break;
                    }
                }
                pos = static_cast<uint16_t>(pos + rowLen);
            }

            if (rowStarts)
                rowStarts[rows] = length;
            return rows;
        }

        // Cluster path: forward walk mirroring the ASCII rules exactly when
        // every cluster is one byte/one cell (last-space-wins break, space
        // hanging off the row end, hard break when no space fits).
        for (;;)
        {
            if (rowStarts)
                rowStarts[rows] = pos;

            const int budget = (rows == 0) ? cols : contCols;
            ++rows;
            if (rows >= maxRows)
                break; // remainder lands on this final row

            int cells = 0;
            uint16_t i = pos;
            uint16_t lastSpaceEnd = 0; // byte pos just past the rightmost break space
            uint16_t fitEnd = pos;     // end of the clusters that fit the budget
            bool exhausted = false;
            for (;;)
            {
                if (i >= length)
                {
                    exhausted = true;
                    break;
                }
                const TextCells::Cluster cl = TextCells::NextCluster(text, i, length);
                if (cells + cl.cells > budget)
                {
                    // The hanging-space rule: a space as the first overflowing
                    // cell hangs invisibly off the row end.
                    if (text[i] == ' ' && cells == budget)
                        lastSpaceEnd = static_cast<uint16_t>(i + 1);
                    break;
                }
                cells += cl.cells;
                i = static_cast<uint16_t>(i + cl.bytes);
                fitEnd = i;
                if (cl.bytes == 1 && text[i - 1] == ' ')
                    lastSpaceEnd = i;
            }
            if (exhausted)
                break; // everything remaining fits this row

            uint16_t rowEnd;
            if (lastSpaceEnd != 0)
                rowEnd = lastSpaceEnd;
            else if (fitEnd > pos)
                rowEnd = fitEnd;
            else // budget smaller than the first cluster: force progress
                rowEnd = static_cast<uint16_t>(pos + TextCells::NextCluster(text, pos, length).bytes);
            pos = rowEnd;
        }

        if (rowStarts)
            rowStarts[rows] = length;
        return rows;
    }
}

Renderer::Renderer() = default;

Renderer::~Renderer()
{
    Shutdown();
}

bool Renderer::Initialize(HWND parent, int width, int height, float dpiScale)
{
    m_dpiScale = dpiScale > 0.0f ? dpiScale : 1.0f;
    if (width < 1) width = 1;
    if (height < 1) height = 1;

    m_width = width;
    m_height = height;
    m_viewWidthDips = static_cast<float>(width) / m_dpiScale;
    m_viewHeightDips = static_cast<float>(height) / m_dpiScale;

    if (!CreateChildWindow(parent, width, height))
        return false;
    if (!CreateD3D11Device())
        return false;
    if (!CreateSwapChain(width, height))
        return false;
    if (!CreateRenderTargetFromBackBuffer())
        return false;
    if (!CreateDWriteResources())
        return false;

    m_dirty = true;
    return true;
}

void Renderer::Shutdown()
{
    ReleaseDeviceResources();
    if (m_hwnd)
    {
        DestroyWindow(m_hwnd);
        m_hwnd = nullptr;
    }
}

// Everything holding a reference to the swapchain's back buffer — required
// to be released before ResizeBuffers. The device context, its brushes, and
// D2D's device-level caches deliberately persist across resizes.
void Renderer::ReleaseBackBufferResources()
{
    if (m_renderTarget)
        m_renderTarget->SetTarget(nullptr);
    SafeRelease(m_targetBitmap);
    SafeRelease(m_surface);
}

void Renderer::ReleaseDeviceResources()
{
    for (auto& f : m_textFormats)
    {
        SafeRelease(f);
    }
    SafeRelease(m_fontFace);
    ClearClusterCache(); // layouts are factory objects: release before it
    SafeRelease(m_dwriteFactory);
    SafeRelease(m_scratchBrush);
    SafeRelease(m_defaultFgBrush);
    SafeRelease(m_selectionBrush);
    ReleaseBackBufferResources();
    SafeRelease(m_atlasContext);
    SafeRelease(m_renderTarget);
    SafeRelease(m_d2dDevice);
    SafeRelease(m_d2dFactory);
    SafeRelease(m_swapChain);
    SafeRelease(m_d3dContext);
    SafeRelease(m_d3d11Device);
}

bool Renderer::CreateD3D11Device()
{
    D3D_FEATURE_LEVEL featureLevels[] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0 };
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
#ifdef _DEBUG
    flags |= D3D11_CREATE_DEVICE_DEBUG;
#endif
    HRESULT hr = D3D11CreateDevice(
        nullptr,
        D3D_DRIVER_TYPE_HARDWARE,
        nullptr,
        flags,
        featureLevels,
        ARRAYSIZE(featureLevels),
        D3D11_SDK_VERSION,
        &m_d3d11Device,
        nullptr,
        &m_d3dContext);
    if (FAILED(hr))
    {
        // Fallback to WARP
        hr = D3D11CreateDevice(
            nullptr,
            D3D_DRIVER_TYPE_WARP,
            nullptr,
            flags,
            featureLevels,
            ARRAYSIZE(featureLevels),
            D3D11_SDK_VERSION,
            &m_d3d11Device,
            nullptr,
            &m_d3dContext);
    }
    return SUCCEEDED(hr);
}

bool Renderer::CreateChildWindow(HWND parent, int width, int height)
{
    if (!parent || !EnsureChildClass())
        return false;

    m_hwnd = CreateWindowExW(0, ChildClassName, L"", WS_CHILD | WS_VISIBLE,
        0, 0, width, height, parent, nullptr, ThisModule(), nullptr);
    return m_hwnd != nullptr;
}

bool Renderer::CreateSwapChain(int width, int height)
{
    IDXGIDevice* dxgiDevice = nullptr;
    HRESULT hr = m_d3d11Device->QueryInterface(__uuidof(IDXGIDevice),
        reinterpret_cast<void**>(&dxgiDevice));
    if (FAILED(hr))
        return false;

    IDXGIAdapter* adapter = nullptr;
    hr = dxgiDevice->GetAdapter(&adapter);
    dxgiDevice->Release();
    if (FAILED(hr))
        return false;

    IDXGIFactory2* factory = nullptr;
    hr = adapter->GetParent(__uuidof(IDXGIFactory2), reinterpret_cast<void**>(&factory));
    adapter->Release();
    if (FAILED(hr))
        return false;

    DXGI_SWAP_CHAIN_DESC1 desc = {};
    desc.Width = static_cast<UINT>(width);
    desc.Height = static_cast<UINT>(height);
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.BufferCount = 2;
    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    desc.Scaling = DXGI_SCALING_NONE;
    desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;

    hr = factory->CreateSwapChainForHwnd(m_d3d11Device, m_hwnd, &desc,
        nullptr, nullptr, &m_swapChain);
    if (SUCCEEDED(hr))
        factory->MakeWindowAssociation(m_hwnd, DXGI_MWA_NO_ALT_ENTER);
    factory->Release();
    return SUCCEEDED(hr);
}

bool Renderer::CreateRenderTargetFromBackBuffer()
{
    SafeRelease(m_surface);
    // Flip model keeps buffer 0 as the writable back buffer across Presents,
    // so the D2D target created on it stays valid until the next resize.
    HRESULT hr = m_swapChain->GetBuffer(0, __uuidof(IDXGISurface),
        reinterpret_cast<void**>(&m_surface));
    if (FAILED(hr))
        return false;
    return CreateD2DRenderTarget();
}

bool Renderer::CreateD2DRenderTarget()
{
    // One-time: factory -> device -> device context. These persist across
    // resizes so D2D's device-level glyph/rasterization caches (color emoji
    // re-rasterization is expensive) and the brushes survive; only the target
    // bitmap below rebinds per resize.
    if (!m_d2dFactory)
    {
        HRESULT hrFactory = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, &m_d2dFactory);
        if (FAILED(hrFactory))
            return false;
    }
    if (!m_renderTarget)
    {
        ID2D1Factory1* factory1 = nullptr;
        if (FAILED(m_d2dFactory->QueryInterface(__uuidof(ID2D1Factory1),
                reinterpret_cast<void**>(&factory1))) || !factory1)
            return false;

        IDXGIDevice* dxgiDevice = nullptr;
        HRESULT hr = m_d3d11Device->QueryInterface(__uuidof(IDXGIDevice),
            reinterpret_cast<void**>(&dxgiDevice));
        if (SUCCEEDED(hr))
        {
            hr = factory1->CreateDevice(dxgiDevice, &m_d2dDevice);
            dxgiDevice->Release();
        }
        factory1->Release();
        if (FAILED(hr))
            return false;

        if (FAILED(m_d2dDevice->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,
                &m_renderTarget)))
            return false;

        // Atlas context: renders cluster bitmaps while the main context is
        // inside BeginDraw. Grayscale AA — ClearType needs an opaque
        // backing, and atlas bitmaps are transparent.
        if (FAILED(m_d2dDevice->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,
                &m_atlasContext)))
            return false;
        m_atlasContext->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);

        // Color emoji (COLR glyphs) need the D2D 1.3 pipeline;
        // ID2D1DeviceContext4 signposts it. DrawText returns void, so gate
        // the flag on this probe instead of passing it blind.
        m_drawTextOptions = D2D1_DRAW_TEXT_OPTIONS_CLIP;
        ID2D1DeviceContext4* dc4 = nullptr;
        if (SUCCEEDED(m_renderTarget->QueryInterface(__uuidof(ID2D1DeviceContext4),
                reinterpret_cast<void**>(&dc4))) && dc4)
        {
            m_drawTextOptions = static_cast<D2D1_DRAW_TEXT_OPTIONS>(
                m_drawTextOptions | D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
            dc4->Release();
        }
    }

    // Per-resize: bind the swapchain's back-buffer surface as the target.
    // Layout math stays in DIPs; the DPI maps DIPs onto the pixel surface.
    D2D1_BITMAP_PROPERTIES1 props = {};
    props.pixelFormat.format = DXGI_FORMAT_B8G8R8A8_UNORM;
    props.pixelFormat.alphaMode = D2D1_ALPHA_MODE_IGNORE;
    props.dpiX = 96.0f * m_dpiScale;
    props.dpiY = 96.0f * m_dpiScale;
    props.bitmapOptions = D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW;

    ID2D1Bitmap1* bitmap = nullptr;
    if (FAILED(m_renderTarget->CreateBitmapFromDxgiSurface(m_surface, &props, &bitmap)))
        return false;
    m_renderTarget->SetTarget(bitmap);
    m_targetBitmap = bitmap;
    m_renderTarget->SetDpi(96.0f * m_dpiScale, 96.0f * m_dpiScale);
    return true;
}

bool Renderer::CreateDWriteResources()
{
    HRESULT hr = DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
        reinterpret_cast<IUnknown**>(&m_dwriteFactory));
    if (FAILED(hr))
        return false;

    return BuildFontResources();
}

bool Renderer::BuildFontResources()
{
    for (int i = 0; i < 4; ++i)
    {
        DWRITE_FONT_WEIGHT weight = (i & 0x01) ? DWRITE_FONT_WEIGHT_BOLD : DWRITE_FONT_WEIGHT_NORMAL;
        DWRITE_FONT_STYLE style = (i & 0x02) ? DWRITE_FONT_STYLE_ITALIC : DWRITE_FONT_STYLE_NORMAL;

        IDWriteTextFormat* format = nullptr;
        HRESULT hr = m_dwriteFactory->CreateTextFormat(m_fontFamily.c_str(), nullptr, weight, style,
            DWRITE_FONT_STRETCH_NORMAL, m_fontSize, L"", &format);
        if (FAILED(hr))
            return false;

        format->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
        m_textFormats[i] = format;
    }

    UpdateLineHeight();

    // Column-based wrap math relies on the monospace advance width; measure one
    // character rather than trusting a hard-coded ratio.
    const float charWidth = MeasureSegment("0", 1, 0);
    m_charWidthDips = charWidth > 0.0f ? charWidth : m_fontSize * 0.6f;

    // Attempt to cache a font face for the normal style.
    IDWriteFontCollection* collection = nullptr;
    if (SUCCEEDED(m_dwriteFactory->GetSystemFontCollection(&collection)))
    {
        UINT32 index = 0;
        BOOL exists = FALSE;
        if (SUCCEEDED(collection->FindFamilyName(m_fontFamily.c_str(), &index, &exists)) && exists)
        {
            IDWriteFontFamily* family = nullptr;
            if (SUCCEEDED(collection->GetFontFamily(index, &family)))
            {
                IDWriteFont* font = nullptr;
                if (SUCCEEDED(family->GetFirstMatchingFont(DWRITE_FONT_WEIGHT_NORMAL,
                    DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE_NORMAL, &font)))
                {
                    font->CreateFontFace(&m_fontFace);
                    font->Release();
                }
                family->Release();
            }
        }
        collection->Release();
    }

    // Pin every DrawText call — primary font or emoji fallback — to one
    // baseline, so per-cluster chunk draws (DrawSegment) line up. For the
    // primary font this reproduces default spacing exactly: its natural line
    // box starts at the layout-rect top with the baseline at its ascent.
    m_baselineY = m_fontSize * 0.8f;
    if (m_fontFace)
    {
        DWRITE_FONT_METRICS metrics = {};
        m_fontFace->GetMetrics(&metrics);
        if (metrics.designUnitsPerEm > 0)
            m_baselineY = metrics.ascent *
                (m_fontSize / static_cast<float>(metrics.designUnitsPerEm));
    }
    for (auto* f : m_textFormats)
    {
        if (f)
            f->SetLineSpacing(DWRITE_LINE_SPACING_METHOD_UNIFORM, m_lineHeight, m_baselineY);
    }

    return true;
}

void Renderer::UpdateLineHeight()
{
    m_lineHeight = m_fontSize * LineHeightRatio;
    m_underlineY = m_fontSize * 1.1f;
    m_underlineThickness = 1.0f;
    if (m_fontFace)
    {
        DWRITE_FONT_METRICS metrics = {};
        m_fontFace->GetMetrics(&metrics);
        if (metrics.designUnitsPerEm > 0)
        {
            const float ratio = m_fontSize / static_cast<float>(metrics.designUnitsPerEm);
            m_lineHeight = (metrics.ascent + metrics.descent + metrics.lineGap) * ratio;
            // underlinePosition is negative (below baseline).
            m_underlineY = (metrics.ascent - metrics.underlinePosition) * ratio;
            m_underlineThickness = std::max(metrics.underlineThickness * ratio, 1.0f);
        }
    }
    m_underlineY = std::min(m_underlineY, m_lineHeight - m_underlineThickness);
}

void Renderer::EnsureBrushes()
{
    if (!m_renderTarget)
        return;

    if (!m_defaultFgBrush)
    {
        D2D1_COLOR_F fgColor = ColorFromU32(m_fgColor);
        m_renderTarget->CreateSolidColorBrush(&fgColor, nullptr, &m_defaultFgBrush);
    }
    if (!m_selectionBrush)
    {
        // Translucent so the tint overlays the already-drawn glyphs.
        D2D1_COLOR_F selColor = ColorFromU32(m_selectionColor);
        m_renderTarget->CreateSolidColorBrush(&selColor, nullptr, &m_selectionBrush);
    }
    if (!m_scratchBrush)
    {
        D2D1_COLOR_F initial = { 1.0f, 1.0f, 1.0f, 1.0f };
        m_renderTarget->CreateSolidColorBrush(&initial, nullptr, &m_scratchBrush);
    }
}

IDWriteTextFormat* Renderer::GetTextFormat(uint8_t flags) const
{
    int index = (flags & 0x03);
    return m_textFormats[index];
}

ID2D1Bitmap1* Renderer::GetClusterBitmap(const wchar_t* key, uint16_t len,
    uint8_t fmtIndex, uint8_t cells, uint32_t fgArgb)
{
    if (len == 0 || len > ClusterKeyMaxLen || !m_dwriteFactory || !m_atlasContext)
        return nullptr;

    // FNV-1a over the UTF-16 units + format index + color.
    uint32_t h = 2166136261u;
    for (uint16_t k = 0; k < len; ++k)
    {
        h ^= static_cast<uint16_t>(key[k]);
        h *= 16777619u;
    }
    h ^= fmtIndex;
    h *= 16777619u;
    h ^= fgArgb;
    h *= 16777619u;

    ClusterEntry& e = m_clusterCache[h & (ClusterCacheSize - 1)];
    if (e.bitmap && e.keyLen == len && e.fmt == fmtIndex && e.fgArgb == fgArgb &&
        std::memcmp(e.key, key, len * sizeof(wchar_t)) == 0)
        return e.bitmap;

    IDWriteTextFormat* format = GetTextFormat(fmtIndex);
    if (!format)
        return nullptr;

    // Box: +1 cell of width slack preserves the natural-advance overhang;
    // one line height reproduces DrawText's vertical clamp. NO_WRAP and the
    // uniform line spacing are inherited from the format, so the baseline
    // inside the bitmap matches direct drawing.
    const float wDips = static_cast<float>(cells + 1) * m_charWidthDips;
    const float hDips = m_lineHeight;

    IDWriteTextLayout* layout = nullptr; // transient: shaping happens once here
    if (FAILED(m_dwriteFactory->CreateTextLayout(key, len, format, wDips, hDips, &layout)) ||
        !layout)
        return nullptr;

    D2D1_BITMAP_PROPERTIES1 props = {};
    props.pixelFormat.format = DXGI_FORMAT_B8G8R8A8_UNORM;
    props.pixelFormat.alphaMode = D2D1_ALPHA_MODE_PREMULTIPLIED;
    props.dpiX = 96.0f * m_dpiScale;
    props.dpiY = 96.0f * m_dpiScale;
    props.bitmapOptions = D2D1_BITMAP_OPTIONS_TARGET; // drawable render target

    const D2D1_SIZE_U px = {
        static_cast<UINT32>(std::ceil(wDips * m_dpiScale)),
        static_cast<UINT32>(std::ceil(hDips * m_dpiScale))
    };
    ID2D1Bitmap1* bitmap = nullptr;
    HRESULT hr = m_atlasContext->CreateBitmap(px, nullptr, 0, &props, &bitmap);
    if (FAILED(hr) || !bitmap)
    {
        layout->Release();
        return nullptr;
    }

    // Render the cluster once on the atlas context (the main context may be
    // inside BeginDraw; two contexts on one device draw independently and
    // share the brushes). Color glyphs ignore the brush; monochrome fallback
    // glyphs bake fgArgb, which is part of the cache key.
    m_atlasContext->SetTarget(bitmap);
    m_atlasContext->SetDpi(96.0f * m_dpiScale, 96.0f * m_dpiScale);
    m_atlasContext->BeginDraw();
    D2D1_COLOR_F clear = { 0.0f, 0.0f, 0.0f, 0.0f };
    m_atlasContext->Clear(&clear);
    if (m_scratchBrush)
    {
        m_scratchBrush->SetColor(ColorFromU32(fgArgb));
        m_atlasContext->DrawTextLayout(D2D1_POINT_2F{ 0.0f, 0.0f }, layout,
            m_scratchBrush, m_drawTextOptions);
    }
    hr = m_atlasContext->EndDraw();
    m_atlasContext->SetTarget(nullptr);
    layout->Release();
    if (FAILED(hr))
    {
        bitmap->Release();
        return nullptr;
    }

    SafeRelease(e.bitmap); // direct-mapped: evict the collision victim
    std::memcpy(e.key, key, len * sizeof(wchar_t));
    e.keyLen = static_cast<uint8_t>(len);
    e.fmt = fmtIndex;
    e.fgArgb = fgArgb;
    e.bitmap = bitmap;
    return e.bitmap;
}

void Renderer::ClearClusterCache()
{
    for (auto& e : m_clusterCache)
    {
        SafeRelease(e.bitmap);
        e.keyLen = 0;
    }
}

float Renderer::MeasureSegment(const char* text, uint16_t length, uint8_t flags)
{
    if (!m_dwriteFactory || length == 0)
        return 0.0f;

    IDWriteTextFormat* format = GetTextFormat(flags);
    if (!format)
        return 0.0f;

    // Deliberately uninitialized: only [0, length) is written, and the
    // DWrite calls below receive the explicit length.
    wchar_t buffer[IrcLineTextSize];
    for (uint16_t i = 0; i < length; ++i)
        buffer[i] = static_cast<wchar_t>(static_cast<unsigned char>(text[i]));

    IDWriteTextLayout* layout = nullptr;
    HRESULT hr = m_dwriteFactory->CreateTextLayout(buffer, length, format,
        10000.0f, m_lineHeight, &layout);
    if (FAILED(hr))
        return 0.0f;

    DWRITE_TEXT_METRICS metrics = {};
    layout->GetMetrics(&metrics);
    layout->Release();
    // metrics.width excludes trailing whitespace; segments often end in a space
    // (format toggles follow the space), and advancing by the bare width glues
    // the next segment to this one. Bg fills should cover the space too.
    return metrics.widthIncludingTrailingWhitespace;
}

void Renderer::DrawSegment(const char* text, uint16_t length, float x, float y,
    float segWidth, uint32_t fg, uint32_t bg, uint8_t flags, bool asciiOnly)
{
    if (!m_renderTarget || length == 0)
        return;

    IDWriteTextFormat* format = GetTextFormat(flags);
    if (!format)
        return;

    // Alpha 0 is the "default" sentinel from the parser: no bg fill, default
    // fg brush. Anything else is an exact color via the scratch brush (D2D
    // snapshots brush state per draw call, so SetColor per segment is safe).
    if (bg & 0xFF000000u)
    {
        m_scratchBrush->SetColor(ColorFromU32(bg));
        D2D1_RECT_F bgRect = Rect(x, y, segWidth, m_lineHeight);
        m_renderTarget->FillRectangle(&bgRect, m_scratchBrush);
    }

    ID2D1SolidColorBrush* brush = m_defaultFgBrush;
    if (fg & 0xFF000000u)
    {
        m_scratchBrush->SetColor(ColorFromU32(fg));
        brush = m_scratchBrush;
    }

    // Deliberately uninitialized: only the written prefix is read, and the
    // DWrite calls below receive explicit lengths. 512 UTF-8 bytes always
    // decode to <= 512 UTF-16 units, so the buffer bound holds on all paths.
    wchar_t buffer[IrcLineTextSize];

    if (asciiOnly)
    {
        for (uint16_t i = 0; i < length; ++i)
            buffer[i] = static_cast<wchar_t>(static_cast<unsigned char>(text[i]));

        D2D1_RECT_F layoutRect = Rect(x, y, m_viewWidthDips - x, m_lineHeight);
        m_renderTarget->DrawText(buffer, length, format, &layoutRect, brush,
            m_drawTextOptions);
    }
    else
    {
        // Cell-grid chunking: contiguous ASCII draws as one DrawText; each
        // non-ASCII cluster draws individually, re-anchored at its cell x so
        // natural-advance mismatch never accumulates across the row. All
        // chunks share one baseline via the formats' uniform line spacing.
        uint16_t i = 0;
        float cellX = 0.0f;
        while (i < length)
        {
            if (!(static_cast<unsigned char>(text[i]) & 0x80))
            {
                uint16_t e = i;
                while (e < length && !(static_cast<unsigned char>(text[e]) & 0x80))
                    ++e;
                // If the span is followed by a zero-width code point
                // (combining mark, VS16, ZWJ), hand the last ASCII char to
                // the cluster path so base + marks shape as one unit.
                if (e < length && e > i &&
                    TextCells::CellWidth(TextCells::DecodeUtf8(text, e, length).cp) == 0)
                    --e;
                if (e > i)
                {
                    for (uint16_t k = 0; k < e - i; ++k)
                        buffer[k] = static_cast<wchar_t>(text[i + k]);
                    D2D1_RECT_F rect = Rect(x + cellX, y, m_viewWidthDips - (x + cellX), m_lineHeight);
                    m_renderTarget->DrawText(buffer, static_cast<UINT32>(e - i), format,
                        &rect, brush, m_drawTextOptions);
                    cellX += static_cast<float>(e - i) * m_charWidthDips;
                    i = e;
                    continue;
                }
                // Empty span: a lone ASCII char owned by a following
                // zero-width mark — the cluster path below folds them.
            }

            const TextCells::Cluster cl = TextCells::NextCluster(text, i, length);
            const uint16_t wlen = TextCells::Utf8ToUtf16(text + i, cl.bytes, buffer);
            const uint32_t effFg = (fg & 0xFF000000u) ? fg : m_fgColor;
            ID2D1Bitmap1* cached = GetClusterBitmap(buffer, wlen,
                static_cast<uint8_t>(flags & 0x03), cl.cells, effFg);
            if (cached)
            {
                // Pre-rasterized: per frame this is a plain bitmap blit —
                // no shaping, no fallback, no color-glyph layer translation.
                const D2D1_SIZE_F bs = cached->GetSize();
                const D2D1_RECT_F dst = Rect(x + cellX, y, bs.width, bs.height);
                m_renderTarget->DrawBitmap(cached, &dst, 1.0f,
                    D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, nullptr);
            }
            else
            {
                // Oversized cluster or creation failure: uncached path.
                D2D1_RECT_F rect = Rect(x + cellX, y, m_viewWidthDips - (x + cellX), m_lineHeight);
                m_renderTarget->DrawText(buffer, wlen, format, &rect, brush,
                    m_drawTextOptions);
            }
            cellX += static_cast<float>(cl.cells) * m_charWidthDips;
            i = static_cast<uint16_t>(i + cl.bytes);
        }
    }

    if (flags & 0x04)
    {
        D2D1_RECT_F ul = Rect(x, y + m_underlineY, segWidth, m_underlineThickness);
        m_renderTarget->FillRectangle(&ul, brush);
    }
}

bool Renderer::AddLine(const char* text, int length)
{
    if (!text || length <= 0)
        return false;
    if (length > static_cast<int>(IrcLineTextSize))
        length = static_cast<int>(IrcLineTextSize);

    // Enqueue a copy so the caller doesn't need to keep text alive.
    return m_inputQueue.Enqueue(text, static_cast<uint16_t>(length));
}

void Renderer::GetColumns(int& cols, int& contCols) const
{
    cols = static_cast<int>((m_viewWidthDips - LeftPadDips - RightPadDips) / m_charWidthDips);
    if (cols < 1)
        cols = 1;
    contCols = cols > ContinuationIndentChars ? cols - ContinuationIndentChars : 1;
}

void Renderer::RewrapAll()
{
    int cols = 0, contCols = 0;
    GetColumns(cols, contCols);

    m_totalRows = 0;
    const uint32_t count = m_ringBuffer.Count();
    for (uint32_t i = 0; i < count; ++i)
    {
        const LineView line = m_ringBuffer.Get(i);
        const uint16_t rows = ComputeWrapRows(line.text, line.length,
            !(line.flags & LineFlagNonAscii), cols, contCols,
            nullptr, IrcLineTextSize);
        m_ringBuffer.SetRowCount(i, rows);
        m_totalRows += rows;
    }
}

void Renderer::ProcessInputQueue()
{
    char buffer[IrcLineTextSize];
    uint16_t length = 0;
    int processed = 0;
    uint32_t rowsAdded = 0;

    int cols = 0, contCols = 0;
    GetColumns(cols, contCols);

    LineSlot scratch;
    while (processed < MaxInputBatch && m_inputQueue.Dequeue(buffer, length))
    {
        scratch.length = 0;
        scratch.segmentCount = 0; // Parse appends from these; text needs no reset
        IrcParser::Parse(&scratch, buffer, length, m_fgColor, m_bgColor);
        scratch.rowCount = ComputeWrapRows(scratch.text, scratch.length,
            !(scratch.flags & LineFlagNonAscii), cols, contCols,
            nullptr, IrcLineTextSize);
        m_totalRows -= m_ringBuffer.Append(scratch); // rows of any evicted lines
        m_totalRows += scratch.rowCount;
        rowsAdded += scratch.rowCount;
        ++processed;
    }

    if (processed > 0)
    {
        m_dirty = true;
        if (m_autoScroll)
        {
            m_scrollOffsetDips = 0.0;
        }
        else
        {
            // Content grows at the bottom; grow the bottom-distance by the rows
            // added so the lines being read stay stationary on screen.
            m_scrollOffsetDips += static_cast<double>(rowsAdded) * m_lineHeight;
        }
        ClampScroll();
    }
}

FrameResult Renderer::RenderFrame()
{
    FrameResult result = {};
    result.dirtyX = 0;
    result.dirtyY = 0;
    result.dirtyW = m_width;
    result.dirtyH = m_height;
    result.rendered = false;

    // Deferred font change: must precede ProcessInputQueue, whose wrap math
    // reads m_charWidthDips.
    if (m_fontDirty && m_dwriteFactory)
    {
        m_fontDirty = false;
        for (auto& f : m_textFormats)
            SafeRelease(f);
        SafeRelease(m_fontFace);
        ClearClusterCache();  // cached layouts bake the old font's metrics
        BuildFontResources(); // best-effort: a failure leaves formats null,
                              // which DrawSegment/MeasureSegment handle
        RewrapAll();          // column width may have changed
    }

    ProcessInputQueue();

    if (!m_renderTarget || !m_dirty)
        return result;

    EnsureBrushes();

    m_renderTarget->BeginDraw();
    D2D1_COLOR_F clearColor = ColorFromU32(m_bgColor);
    m_renderTarget->Clear(&clearColor);

    const uint32_t count = m_ringBuffer.Count();
    ClampScroll();

    const float viewTop = ViewTopDips();

    int firstRow = static_cast<int>(std::floor(viewTop / m_lineHeight));
    if (firstRow < 0)
        firstRow = 0;

    int cols = 0, contCols = 0;
    GetColumns(cols, contCols);

    // Locate the line containing the first visible global row.
    uint32_t li = 0;
    uint32_t rowAcc = 0;
    while (li < count)
    {
        const LineView slot = m_ringBuffer.Get(li);
        const uint32_t rc = slot.rowCount > 0 ? slot.rowCount : 1;
        if (rowAcc + rc > static_cast<uint32_t>(firstRow))
            break;
        rowAcc += rc;
        ++li;
    }
    uint32_t rowInLine = static_cast<uint32_t>(firstRow) - rowAcc;

    uint64_t selStartLine = 0, selEndLine = 0;
    uint16_t selStartOffset = 0, selEndOffset = 0;
    const bool hasSelection = GetSelectionRange(selStartLine, selStartOffset,
        selEndLine, selEndOffset);
    const uint64_t evictedTotal = m_ringBuffer.EvictedTotal();

    float y = static_cast<float>(firstRow) * m_lineHeight - viewTop;
    uint16_t rowStarts[IrcLineTextSize + 1];

    for (; li < count && y < m_viewHeightDips; ++li)
    {
        const LineView slot = m_ringBuffer.Get(li);
        const bool ascii = !(slot.flags & LineFlagNonAscii);

        const uint16_t rows = ComputeWrapRows(slot.text, slot.length, ascii,
            cols, contCols, rowStarts, IrcLineTextSize);

        for (uint16_t r = static_cast<uint16_t>(rowInLine);
             r < rows && y < m_viewHeightDips; ++r, y += m_lineHeight)
        {
            const uint16_t rowStart = rowStarts[r];
            const uint16_t rowEnd = rowStarts[r + 1];
            float x = LeftPadDips +
                (r > 0 ? static_cast<float>(ContinuationIndentChars) * m_charWidthDips : 0.0f);

            if (slot.segmentCount == 0)
            {
                // No segments parsed: draw as plain text (no bg fill, width unused)
                DrawSegment(slot.text + rowStart, rowEnd - rowStart, x, y, 0.0f,
                    IrcPalette::Default, IrcPalette::Default, 0, ascii);
            }
            else
            {
                for (uint8_t s = 0; s < slot.segmentCount; ++s)
                {
                    const Segment& seg = slot.segments[s];
                    uint16_t segEnd = (s + 1 < slot.segmentCount) ? slot.segments[s + 1].offset : slot.length;
                    if (segEnd > slot.length)
                        segEnd = slot.length;

                    // Portion of this segment that falls inside the current row.
                    // A segment split across rows keeps its formatting on both.
                    const uint16_t runStart = seg.offset > rowStart ? seg.offset : rowStart;
                    const uint16_t runEnd = segEnd < rowEnd ? segEnd : rowEnd;
                    if (runEnd <= runStart)
                        continue;

                    // Monospace grid: advance and bg-fill width are cell
                    // arithmetic, no per-frame DWrite measurement.
                    const float runWidth = ascii
                        ? static_cast<float>(runEnd - runStart) * m_charWidthDips
                        : static_cast<float>(TextCells::CountCells(slot.text, runStart, runEnd)) * m_charWidthDips;
                    DrawSegment(slot.text + runStart, runEnd - runStart, x, y,
                        runWidth, seg.fg, seg.bg, seg.flags, ascii);
                    x += runWidth;
                }
            }

            // Selection highlight: a translucent tint over the drawn glyphs.
            // mIRC rule: a multi-line selection covers whole lines; a
            // single-line selection covers its byte range (which may span
            // several wrapped rows of the same logical line).
            if (hasSelection && m_selectionBrush)
            {
                const uint64_t lineId = evictedTotal + li;
                if (lineId >= selStartLine && lineId <= selEndLine)
                {
                    uint16_t hs = rowStart;
                    uint16_t he = rowEnd;
                    if (selStartLine == selEndLine)
                    {
                        hs = selStartOffset > rowStart ? selStartOffset : rowStart;
                        he = selEndOffset < rowEnd ? selEndOffset : rowEnd;
                    }
                    if (he >= hs)
                    {
                        // Cell math: HitTest snaps hs/he to cluster starts,
                        // so both counts land on cluster boundaries.
                        float hw = ascii
                            ? static_cast<float>(he - hs) * m_charWidthDips
                            : static_cast<float>(TextCells::CountCells(slot.text, hs, he)) * m_charWidthDips;
                        // A blank line inside a multi-line selection still
                        // shows a one-cell stub so it reads as selected.
                        if (hw <= 0.0f && selStartLine != selEndLine)
                            hw = m_charWidthDips;
                        if (hw > 0.0f)
                        {
                            const float hcells = ascii
                                ? static_cast<float>(hs - rowStart)
                                : static_cast<float>(TextCells::CountCells(slot.text, rowStart, hs));
                            const float hx = LeftPadDips +
                                (r > 0 ? static_cast<float>(ContinuationIndentChars) * m_charWidthDips : 0.0f) +
                                hcells * m_charWidthDips;
                            const D2D1_RECT_F rect = { hx, y, hx + hw, y + m_lineHeight };
                            m_renderTarget->FillRectangle(&rect, m_selectionBrush);
                        }
                    }
                }
            }
        }
        rowInLine = 0;
    }

    HRESULT hr = m_renderTarget->EndDraw();
    if (hr == D2DERR_RECREATE_TARGET)
    {
        // Surface lost; flag dirty so next frame recreates
        m_dirty = true;
    }
    else
    {
        m_dirty = false;
    }

    // Flip-model present; no vsync wait, DWM composition prevents tearing.
    if (m_swapChain && m_swapChain->Present(0, 0) != S_OK)
        m_dirty = true;

    result.rendered = true;
    return result;
}

void Renderer::SetSize(int width, int height, float dpiScale)
{
    if (dpiScale <= 0.0f)
        dpiScale = 1.0f;
    if (width < 1) width = 1;
    if (height < 1) height = 1;
    if (width == m_width && height == m_height && dpiScale == m_dpiScale)
        return;

    int oldCols = 0, oldContCols = 0;
    GetColumns(oldCols, oldContCols);

    const bool pixelsChanged = (width != m_width) || (height != m_height);
    const bool dpiChanged = (dpiScale != m_dpiScale);

    m_dpiScale = dpiScale;
    m_width = width;
    m_height = height;
    m_viewWidthDips = static_cast<float>(width) / m_dpiScale;
    m_viewHeightDips = static_cast<float>(height) / m_dpiScale;

    if (dpiChanged)
        ClearClusterCache(); // atlas bitmaps bake the pixel density

    if (pixelsChanged && m_swapChain)
    {
        // ResizeBuffers requires every back-buffer reference to be gone first;
        // the device context and brushes persist, only the target rebinds.
        ReleaseBackBufferResources();
        if (SUCCEEDED(m_swapChain->ResizeBuffers(0, static_cast<UINT>(width),
                static_cast<UINT>(height), DXGI_FORMAT_UNKNOWN, 0)))
        {
            CreateRenderTargetFromBackBuffer();
        }
    }
    else if (dpiChanged && m_renderTarget)
    {
        m_renderTarget->SetDpi(96.0f * dpiScale, 96.0f * dpiScale);
    }

    int newCols = 0, newContCols = 0;
    GetColumns(newCols, newContCols);
    if (newCols != oldCols)
        RewrapAll(); // row counts and total content height depend on the column width

    ClampScroll();
    m_dirty = true;
}

float Renderer::MaxScrollDips() const
{
    const float maxScroll = ContentHeightDips() - m_viewHeightDips;
    return maxScroll > 0.0f ? maxScroll : 0.0f;
}

void Renderer::ClampScroll()
{
    if (m_scrollOffsetDips < 0.0)
        m_scrollOffsetDips = 0.0;

    const double maxScroll = static_cast<double>(MaxScrollDips());
    if (m_scrollOffsetDips > maxScroll)
        m_scrollOffsetDips = maxScroll;

    // Treat near-bottom as bottom so auto-scroll re-engages cleanly. The offset
    // is deliberately not pixel-rounded here: the streaming anchor accumulates
    // into it every batch, and repeated rounding would random-walk the view.
    if (m_scrollOffsetDips < 0.5)
        m_scrollOffsetDips = 0.0;
}

void Renderer::ScrollByPixels(float deltaDips)
{
    if (deltaDips == 0.0f)
        return;

    m_scrollOffsetDips += deltaDips;
    ClampScroll();
    // Never re-pin mid-drag: auto-scroll stays frozen until SelectionEnd.
    m_autoScroll = !m_selectionActive && (m_scrollOffsetDips == 0.0);
    m_dirty = true;
}

void Renderer::ScrollToOffset(float offsetDips)
{
    m_scrollOffsetDips = offsetDips;
    ClampScroll();
    m_autoScroll = !m_selectionActive && (m_scrollOffsetDips == 0.0);
    m_dirty = true;
}

void Renderer::ScrollToEnd()
{
    m_scrollOffsetDips = 0.0;
    m_autoScroll = !m_selectionActive;
    m_dirty = true;
}

float Renderer::ViewTopDips() const
{
    // Top of the viewport in content space. Negative when content is shorter
    // than the viewport, which bottom-aligns the content (blank space at top).
    // Snapped to a device pixel so rendering and hit-testing agree exactly.
    const float viewTop = ContentHeightDips() - m_viewHeightDips -
        static_cast<float>(m_scrollOffsetDips);
    return std::round(viewTop * m_dpiScale) / m_dpiScale;
}

bool Renderer::HitTest(float xDips, float yDips, uint64_t& lineId, uint16_t& offset) const
{
    const uint32_t count = m_ringBuffer.Count();
    if (count == 0 || m_totalRows == 0)
        return false;

    const float contentY = ViewTopDips() + yDips;
    int globalRow = static_cast<int>(std::floor(contentY / m_lineHeight));
    if (globalRow < 0)
        globalRow = 0;
    if (globalRow >= static_cast<int>(m_totalRows))
        globalRow = static_cast<int>(m_totalRows) - 1;

    // Locate the line containing the row (same walk as RenderFrame).
    uint32_t li = 0;
    uint32_t rowAcc = 0;
    while (li < count)
    {
        const LineView slot = m_ringBuffer.Get(li);
        const uint32_t rc = slot.rowCount > 0 ? slot.rowCount : 1;
        if (rowAcc + rc > static_cast<uint32_t>(globalRow))
            break;
        rowAcc += rc;
        ++li;
    }
    if (li >= count)
        li = count - 1;

    int cols = 0, contCols = 0;
    GetColumns(cols, contCols);

    const LineView line = m_ringBuffer.Get(li);
    const bool lineAscii = !(line.flags & LineFlagNonAscii);
    uint16_t rowStarts[IrcLineTextSize + 1];
    const uint16_t rows = ComputeWrapRows(line.text, line.length, lineAscii,
        cols, contCols, rowStarts, IrcLineTextSize);

    uint32_t rowInLine = static_cast<uint32_t>(globalRow) >= rowAcc
        ? static_cast<uint32_t>(globalRow) - rowAcc : 0;
    if (rowInLine >= rows)
        rowInLine = rows - 1u;

    const uint16_t rowStart = rowStarts[rowInLine];
    const uint16_t rowEnd = rowStarts[rowInLine + 1];

    // Monospace grid: column is pure arithmetic; round snaps at the character
    // midpoint. X in the pad clamps to row start, past the text to row end.
    const float indent = rowInLine > 0
        ? static_cast<float>(ContinuationIndentChars) * m_charWidthDips : 0.0f;
    lineId = m_ringBuffer.EvictedTotal() + li;

    if (lineAscii)
    {
        int col = static_cast<int>(std::round((xDips - LeftPadDips - indent) / m_charWidthDips));
        const int rowLen = static_cast<int>(rowEnd) - static_cast<int>(rowStart);
        if (col < 0) col = 0;
        if (col > rowLen) col = rowLen;
        offset = static_cast<uint16_t>(rowStart + col);
        return true;
    }

    // Cluster walk: same midpoint rule (identical to round() when every
    // cluster is one cell), and the offset always lands on a cluster start
    // so a selection can never split a multi-byte code point.
    float cellPos = (xDips - LeftPadDips - indent) / m_charWidthDips;
    if (cellPos < 0.0f)
        cellPos = 0.0f;
    float c = 0.0f;
    uint16_t i = rowStart;
    while (i < rowEnd)
    {
        const TextCells::Cluster cl = TextCells::NextCluster(line.text, i, rowEnd);
        const float w = cl.cells > 0 ? static_cast<float>(cl.cells) : 1.0f;
        if (cellPos < c + w * 0.5f)
            break;
        c += static_cast<float>(cl.cells);
        i = static_cast<uint16_t>(i + cl.bytes);
    }
    offset = i;
    return true;
}

bool Renderer::GetSelectionRange(uint64_t& startLine, uint16_t& startOffset,
    uint64_t& endLine, uint16_t& endOffset) const
{
    if (!m_selectionActive)
        return false;

    startLine = m_selAnchorLine;
    startOffset = m_selAnchorOffset;
    endLine = m_selCaretLine;
    endOffset = m_selCaretOffset;
    if (endLine < startLine || (endLine == startLine && endOffset < startOffset))
    {
        std::swap(startLine, endLine);
        std::swap(startOffset, endOffset);
    }

    // Clamp away anything evicted from scrollback mid-drag.
    const uint64_t evicted = m_ringBuffer.EvictedTotal();
    if (endLine < evicted)
        return false;
    if (startLine < evicted)
    {
        startLine = evicted;
        startOffset = 0;
    }

    return !(startLine == endLine && startOffset == endOffset);
}

void Renderer::SelectionBegin(float xDips, float yDips)
{
    uint64_t lineId = 0;
    uint16_t offset = 0;
    if (!HitTest(xDips, yDips, lineId, offset))
        return;

    m_selectionActive = true;
    m_selAnchorLine = m_selCaretLine = lineId;
    m_selAnchorOffset = m_selCaretOffset = offset;
    // Freeze the view for the drag: unpinned, ProcessInputQueue grows the
    // bottom-distance as lines arrive so the text under the cursor stays put.
    m_selWasAutoScroll = m_autoScroll;
    m_autoScroll = false;
    m_dirty = true;
}

void Renderer::SelectionUpdate(float xDips, float yDips)
{
    if (!m_selectionActive)
        return;

    uint64_t lineId = 0;
    uint16_t offset = 0;
    if (!HitTest(xDips, yDips, lineId, offset))
        return;

    if (lineId != m_selCaretLine || offset != m_selCaretOffset)
    {
        m_selCaretLine = lineId;
        m_selCaretOffset = offset;
        m_dirty = true;
    }
}

int Renderer::SelectionGetText(char* buf, int cap) const
{
    uint64_t startLine = 0, endLine = 0;
    uint16_t startOffset = 0, endOffset = 0;
    if (!GetSelectionRange(startLine, startOffset, endLine, endOffset))
        return 0;

    const uint64_t evicted = m_ringBuffer.EvictedTotal();
    int written = 0; // doubles as the required size when buf == nullptr

    auto emit = [&](const char* data, int len)
    {
        if (len <= 0)
            return;
        if (!buf)
        {
            written += len;
            return;
        }
        const int room = cap - written;
        const int n = len < room ? len : room;
        if (n > 0)
        {
            std::memcpy(buf + written, data, n);
            written += n;
        }
    };

    if (startLine == endLine)
    {
        const LineView line = m_ringBuffer.Get(static_cast<uint32_t>(startLine - evicted));
        uint16_t s = startOffset, e = endOffset;
        if (e > line.length) e = line.length;
        if (s > e) s = e;
        emit(line.text + s, e - s);
    }
    else
    {
        // mIRC rule: a multi-line selection copies whole lines, CRLF-joined.
        for (uint64_t id = startLine; id <= endLine; ++id)
        {
            const LineView line = m_ringBuffer.Get(static_cast<uint32_t>(id - evicted));
            if (id > startLine)
                emit("\r\n", 2);
            emit(line.text, line.length);
        }
    }
    return written;
}

void Renderer::SelectionEnd()
{
    if (!m_selectionActive)
        return;

    m_selectionActive = false;
    if (m_selWasAutoScroll)
        ScrollToEnd(); // resume following new lines, like mIRC
    m_dirty = true;
}

void Renderer::Clear()
{
    m_ringBuffer.Clear();
    m_totalRows = 0;
    m_scrollOffsetDips = 0.0;
    m_selectionActive = false; // anchors reference lines that no longer exist
    m_autoScroll = true;
    m_dirty = true;
}

void Renderer::SetBackgroundColor(uint32_t argb)
{
    argb |= 0xFF000000u; // opaque swapchain: force alpha
    if (argb == m_bgColor)
        return;
    m_bgColor = argb;
    m_dirty = true;
}

void Renderer::SetForegroundColor(uint32_t argb)
{
    argb |= 0xFF000000u;
    if (argb == m_fgColor)
        return;
    m_fgColor = argb;
    if (m_defaultFgBrush)
        m_defaultFgBrush->SetColor(ColorFromU32(m_fgColor));
    m_dirty = true;
}

void Renderer::SetSelectionColor(uint32_t argb)
{
    if (argb == m_selectionColor)
        return;
    m_selectionColor = argb; // alpha preserved: the tint overlays drawn glyphs
    if (m_selectionBrush)
        m_selectionBrush->SetColor(ColorFromU32(m_selectionColor));
    m_dirty = true;
}

// Font changes only mark m_fontDirty; the (expensive) format rebuild and
// full rewrap run once at the top of the next RenderFrame. This coalesces a
// back-to-back family+size change into a single rebuild, and makes re-applying
// the current values free. Scroll metrics reflect the old font for at most one
// frame; the scrollbar syncs per-frame via FrameRendered anyway.
void Renderer::SetFontFamily(const wchar_t* family)
{
    if (!family || !family[0] || m_fontFamily == family)
        return;

    m_fontFamily = family;
    m_fontDirty = true;
    m_dirty = true;
}

void Renderer::SetFontSize(float size)
{
    if (size <= 0.0f || size == m_fontSize)
        return;

    m_fontSize = size;
    m_fontDirty = true;
    m_dirty = true;
}

void Renderer::SetMaxLines(uint32_t maxLines)
{
    m_totalRows -= m_ringBuffer.SetMaxLines(maxLines); // rows of evicted lines
    ClampScroll(); // content may have shrunk under the current offset
    m_dirty = true;
}

void Renderer::GetScrollInfo(float* contentHeight, float* viewportHeight,
    float* scrollOffset, float* lineHeight, int* pinned) const
{
    if (contentHeight) *contentHeight = ContentHeightDips();
    if (viewportHeight) *viewportHeight = m_viewHeightDips;
    if (scrollOffset) *scrollOffset = static_cast<float>(m_scrollOffsetDips);
    if (lineHeight) *lineHeight = m_lineHeight;
    if (pinned) *pinned = m_autoScroll ? 1 : 0;
}

// C exports
extern "C" __declspec(dllexport) Renderer* CreateRenderer(HWND parent, int width, int height, float dpiScale)
{
    Renderer* r = new Renderer();
    if (!r->Initialize(parent, width, height, dpiScale))
    {
        delete r;
        return nullptr;
    }
    return r;
}

extern "C" __declspec(dllexport) HWND GetChildHwnd(Renderer* renderer)
{
    return renderer ? renderer->GetChildHwnd() : nullptr;
}

extern "C" __declspec(dllexport) void DestroyRenderer(Renderer* renderer)
{
    delete renderer;
}

extern "C" __declspec(dllexport) bool AddLine(Renderer* renderer, const char* text, int length)
{
    return renderer ? renderer->AddLine(text, length) : false;
}

extern "C" __declspec(dllexport) bool RenderFrame(Renderer* renderer, int* dirtyX, int* dirtyY, int* dirtyW, int* dirtyH)
{
    if (!renderer)
        return false;

    FrameResult r = renderer->RenderFrame();
    if (dirtyX) *dirtyX = r.dirtyX;
    if (dirtyY) *dirtyY = r.dirtyY;
    if (dirtyW) *dirtyW = r.dirtyW;
    if (dirtyH) *dirtyH = r.dirtyH;
    return r.rendered;
}

extern "C" __declspec(dllexport) void SetSize(Renderer* renderer, int width, int height, float dpiScale)
{
    if (renderer) renderer->SetSize(width, height, dpiScale);
}

extern "C" __declspec(dllexport) void ScrollByPixels(Renderer* renderer, float deltaDips)
{
    if (renderer) renderer->ScrollByPixels(deltaDips);
}

extern "C" __declspec(dllexport) void ScrollToOffset(Renderer* renderer, float offsetDips)
{
    if (renderer) renderer->ScrollToOffset(offsetDips);
}

extern "C" __declspec(dllexport) void ScrollToEnd(Renderer* renderer)
{
    if (renderer) renderer->ScrollToEnd();
}

extern "C" __declspec(dllexport) void SetBackgroundColor(Renderer* renderer, uint32_t argb)
{
    if (renderer) renderer->SetBackgroundColor(argb);
}

extern "C" __declspec(dllexport) void SetForegroundColor(Renderer* renderer, uint32_t argb)
{
    if (renderer) renderer->SetForegroundColor(argb);
}

extern "C" __declspec(dllexport) void SetSelectionColor(Renderer* renderer, uint32_t argb)
{
    if (renderer) renderer->SetSelectionColor(argb);
}

extern "C" __declspec(dllexport) void SetFontFamily(Renderer* renderer, const wchar_t* family)
{
    if (renderer) renderer->SetFontFamily(family);
}

extern "C" __declspec(dllexport) void SetFontSize(Renderer* renderer, float size)
{
    if (renderer) renderer->SetFontSize(size);
}

extern "C" __declspec(dllexport) void SetMaxLines(Renderer* renderer, uint32_t maxLines)
{
    if (renderer) renderer->SetMaxLines(maxLines);
}

extern "C" __declspec(dllexport) void Clear(Renderer* renderer)
{
    if (renderer) renderer->Clear();
}

extern "C" __declspec(dllexport) int GetLineCount(Renderer* renderer)
{
    return renderer ? static_cast<int>(renderer->GetLineCount()) : 0;
}

extern "C" __declspec(dllexport) void GetChatScrollInfo(Renderer* renderer, float* contentHeight,
    float* viewportHeight, float* scrollOffset, float* lineHeight, int* pinned)
{
    if (renderer)
    {
        renderer->GetScrollInfo(contentHeight, viewportHeight, scrollOffset, lineHeight, pinned);
    }
    else
    {
        if (contentHeight) *contentHeight = 0.0f;
        if (viewportHeight) *viewportHeight = 0.0f;
        if (scrollOffset) *scrollOffset = 0.0f;
        if (lineHeight) *lineHeight = 0.0f;
        if (pinned) *pinned = 1;
    }
}

extern "C" __declspec(dllexport) void SelectionBegin(Renderer* renderer, float xDips, float yDips)
{
    if (renderer) renderer->SelectionBegin(xDips, yDips);
}

extern "C" __declspec(dllexport) void SelectionUpdate(Renderer* renderer, float xDips, float yDips)
{
    if (renderer) renderer->SelectionUpdate(xDips, yDips);
}

extern "C" __declspec(dllexport) int SelectionGetText(Renderer* renderer, char* buf, int cap)
{
    return renderer ? renderer->SelectionGetText(buf, cap) : 0;
}

extern "C" __declspec(dllexport) void SelectionEnd(Renderer* renderer)
{
    if (renderer) renderer->SelectionEnd();
}
