#pragma once

#include "pch.h"
#include "RingBuffer.h"
#include "MpscQueue.h"

constexpr uint32_t InputQueueCapacity = 4096;
constexpr uint32_t MaxPaletteColors = 16;

struct FrameResult
{
    int dirtyX;
    int dirtyY;
    int dirtyW;
    int dirtyH;
    bool rendered;
};

class Renderer
{
public:
    Renderer();
    ~Renderer();

    Renderer(const Renderer&) = delete;
    Renderer& operator=(const Renderer&) = delete;

    bool Initialize(HWND parent, int width, int height, float dpiScale);
    void Shutdown();

    bool AddLine(const char* text, int length);
    FrameResult RenderFrame();

    // Resizes the swapchain buffers to the new pixel size (the hosted child
    // window itself is positioned by WPF's HwndHost layout).
    void SetSize(int width, int height, float dpiScale);
    void ScrollByPixels(float deltaDips);
    void ScrollToOffset(float offsetDips);
    void ScrollToEnd();
    void Clear();

    // Selection: coordinates are viewport-relative DIPs. Begin freezes
    // auto-scroll for the duration of the drag; End clears the selection and
    // restores it. GetText with buf == nullptr returns the required UTF-8
    // byte count (0 = no selection), otherwise writes up to cap bytes and
    // returns the bytes written.
    void SelectionBegin(float xDips, float yDips);
    void SelectionUpdate(float xDips, float yDips);
    int  SelectionGetText(char* buf, int cap) const;
    void SelectionEnd();

    HWND GetChildHwnd() const { return m_hwnd; }
    uint32_t GetLineCount() const { return m_ringBuffer.Count(); }
    void GetScrollInfo(float* contentHeight, float* viewportHeight,
        float* scrollOffset, float* lineHeight, int* pinned) const;

private:
    bool CreateChildWindow(HWND parent, int width, int height);
    bool CreateD3D11Device();
    bool CreateSwapChain(int width, int height);
    bool CreateRenderTargetFromBackBuffer();
    bool CreateD2DRenderTarget();
    bool CreateDWriteResources();
    void ReleaseBackBufferResources();
    void ReleaseDeviceResources();

    void ProcessInputQueue();
    void UpdateLineHeight();
    void EnsureBrushes();
    void FillPalette();

    // Word-wrap layout is pure column arithmetic (monospace font, byte-per-char).
    void GetColumns(int& cols, int& contCols) const;
    void RewrapAll();

    float ContentHeightDips() const { return static_cast<float>(m_totalRows) * m_lineHeight; }
    float MaxScrollDips() const;
    void ClampScroll();

    float ViewTopDips() const;
    bool HitTest(float xDips, float yDips, uint64_t& lineId, uint16_t& offset) const;
    // Ordered, eviction-clamped selection endpoints. False when the selection
    // is empty (no drag, zero-width, or fully evicted).
    bool GetSelectionRange(uint64_t& startLine, uint16_t& startOffset,
        uint64_t& endLine, uint16_t& endOffset) const;

    void DrawSegment(const char* text, uint16_t length, float x, float y,
        float segWidth, uint8_t fg, uint8_t bg, uint8_t flags);
    float MeasureSegment(const char* text, uint16_t length, uint8_t flags);

    IDWriteTextFormat* GetTextFormat(uint8_t flags) const;

private:
    // Presentation: child HWND + D3D11 flip-model swapchain. m_surface is the
    // swapchain's buffer-0 DXGI surface (flip model keeps index 0 writable).
    HWND                    m_hwnd = nullptr;
    ID3D11Device*           m_d3d11Device = nullptr;
    ID3D11DeviceContext*    m_d3dContext = nullptr;
    IDXGISwapChain1*        m_swapChain = nullptr;
    IDXGISurface*           m_surface = nullptr;

    // D2D
    ID2D1Factory*           m_d2dFactory = nullptr;
    ID2D1RenderTarget*      m_renderTarget = nullptr;
    ID2D1SolidColorBrush*   m_brushes[MaxPaletteColors] = {};
    ID2D1SolidColorBrush*   m_defaultFgBrush = nullptr;
    ID2D1SolidColorBrush*   m_defaultBgBrush = nullptr;
    ID2D1SolidColorBrush*   m_selectionBrush = nullptr;

    // DWrite
    IDWriteFactory*         m_dwriteFactory = nullptr;
    IDWriteTextFormat*      m_textFormats[4] = {}; // bit 0 bold, bit 1 italic
    IDWriteFontFace*        m_fontFace = nullptr;

    // Data
    RingBuffer              m_ringBuffer;
    MpscQueue<InputQueueCapacity> m_inputQueue;

    // Viewport / state
    int                     m_width = 0;         // viewport device pixels
    int                     m_height = 0;        // viewport device pixels
    float                   m_dpiScale = 1.0f;
    float                   m_viewWidthDips = 0.0f;
    float                   m_viewHeightDips = 0.0f;
    float                   m_lineHeight = 16.0f;
    float                   m_fontSize = 14.0f;
    float                   m_charWidthDips = 8.0f;   // monospace advance width
    uint32_t                m_totalRows = 0;          // wrapped rows across all slots
    double                  m_scrollOffsetDips = 0.0; // distance from bottom of content; 0 = pinned
    bool                    m_autoScroll = true;
    bool                    m_dirty = true;

    // Selection (drag in progress). Endpoints are absolute line ids
    // (RingBuffer::EvictedTotal() + logicalIndex) so they survive eviction,
    // plus byte offsets into the line (stable across rewrap).
    bool                    m_selectionActive = false;
    uint64_t                m_selAnchorLine = 0;
    uint16_t                m_selAnchorOffset = 0;
    uint64_t                m_selCaretLine = 0;
    uint16_t                m_selCaretOffset = 0;
    bool                    m_selWasAutoScroll = false; // pin state to restore on end
    uint32_t                m_palette[MaxPaletteColors] = {};

    // Batching
    uint64_t                m_lastInputTime = 0;
};

extern "C" __declspec(dllexport) Renderer* CreateRenderer(HWND parent, int width, int height, float dpiScale);
extern "C" __declspec(dllexport) void DestroyRenderer(Renderer* renderer);
extern "C" __declspec(dllexport) HWND GetChildHwnd(Renderer* renderer);
extern "C" __declspec(dllexport) bool AddLine(Renderer* renderer, const char* text, int length);
extern "C" __declspec(dllexport) bool RenderFrame(Renderer* renderer, int* dirtyX, int* dirtyY, int* dirtyW, int* dirtyH);
extern "C" __declspec(dllexport) void SetSize(Renderer* renderer, int width, int height, float dpiScale);
extern "C" __declspec(dllexport) void ScrollByPixels(Renderer* renderer, float deltaDips);
extern "C" __declspec(dllexport) void ScrollToOffset(Renderer* renderer, float offsetDips);
extern "C" __declspec(dllexport) void ScrollToEnd(Renderer* renderer);
extern "C" __declspec(dllexport) void Clear(Renderer* renderer);
extern "C" __declspec(dllexport) int GetLineCount(Renderer* renderer);
// "Chat" prefix avoids an extern "C" clash with the Win32 GetScrollInfo in winuser.h.
extern "C" __declspec(dllexport) void GetChatScrollInfo(Renderer* renderer, float* contentHeight,
    float* viewportHeight, float* scrollOffset, float* lineHeight, int* pinned);
extern "C" __declspec(dllexport) void SelectionBegin(Renderer* renderer, float xDips, float yDips);
extern "C" __declspec(dllexport) void SelectionUpdate(Renderer* renderer, float xDips, float yDips);
extern "C" __declspec(dllexport) int SelectionGetText(Renderer* renderer, char* buf, int cap);
extern "C" __declspec(dllexport) void SelectionEnd(Renderer* renderer);
