#pragma once

#include "pch.h"
#include "RingBuffer.h"
#include "MpscQueue.h"
#include "Palette.h"

#include <atomic>
#include <mutex>
#include <string>

// 1024 slots (~540 KB): the per-frame drain is MaxInputBatch (1024) lines, so
// the queue absorbs a full 50 ms UI-thread stall at a 20k lines/s producer —
// ~3 frames of headroom at 10x realistic IRC traffic. Overflow drops lines,
// same as any capacity would under a sustained stall.
constexpr uint32_t InputQueueCapacity = 1024;

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

    // View lifecycle. The Renderer object itself is the persistent document
    // (ring buffer + input queue survive for the object's lifetime); the View
    // (child HWND + D3D/D2D/DWrite stack) attaches and detaches as the host
    // control enters and leaves the WPF visual tree. DetachView PARKS the
    // live View (hides the child HWND and reparents it to a hidden holder
    // window) rather than destroying it, so a later AttachView is a
    // SetParent + ShowWindow + possible resize — sub-millisecond, no GPU
    // recreation, no rewrap. Both are UI-thread only.
    bool AttachView(HWND parent, int width, int height, float dpiScale);
    void DetachView();

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

    // Default (theme) colors, 0xAARRGGBB; alpha is forced opaque. Applied at
    // draw time, so existing default-colored content recolors immediately.
    // UI-thread only, like SetSize/Clear.
    void SetBackgroundColor(uint32_t argb);
    void SetForegroundColor(uint32_t argb);

    // Selection highlight tint, 0xAARRGGBB. Alpha is preserved (not forced
    // opaque) since the tint overlays already-drawn glyphs.
    void SetSelectionColor(uint32_t argb);

    // Font family/size, applied immediately (rebuilds text formats, remeasures
    // the monospace column width, and re-wraps all scrollback). Invalid input
    // (null/empty family, non-positive size) is ignored. UI-thread only, like
    // SetSize/Clear. Non-monospace fonts are allowed but render with uneven
    // glyph spacing since layout is fixed-column.
    void SetFontFamily(const wchar_t* family);
    void SetFontSize(float size);

    // Scrollback retention limit, clamped to [1, IrcLineCapacity]. Once
    // reached, the oldest lines are evicted as new lines arrive; shrinking
    // evicts immediately. UI-thread only, like SetSize/Clear.
    void SetMaxLines(uint32_t maxLines);

    // Classic-client color mode: when enabled (default), inbound \x03 indices
    // 16-98 fold onto the basic palette modulo 16 instead of the standardized
    // extended palette. Applies at parse time — new lines only.
    void SetExtendedColorWrap(bool wrap);

    // Returns committed scrollback bytes no longer in use to the OS (see
    // RingBuffer::TrimStorage). Safe attached or detached; intended for the
    // host's idle-time memory trim.
    void TrimStorage();

    // Destroys a parked View (HWND + GPU stack) without touching scrollback;
    // no-op while a View is attached. Used by the managed LRU park limit.
    // The next AttachView rebuilds from scratch. UI-thread only.
    void ReleaseView();

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
    bool BuildFontResources(); // (re)creates m_textFormats/m_fontFace/m_charWidthDips
                               // from m_fontFamily/m_fontSize; assumes m_dwriteFactory exists.
    void ReleaseBackBufferResources();
    void ReleaseDeviceResources();

    void ProcessInputQueue();       // takes m_drainMutex, then drains
    void ProcessInputQueueLocked(); // caller holds m_drainMutex
    void DrainQueuePreView();       // parse/store only (no wrap metrics yet);
                                    // caller holds m_drainMutex
    void UpdateLineHeight();
    void EnsureBrushes();

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
        float segWidth, uint32_t fg, uint32_t bg, uint8_t flags, bool asciiOnly);
    float MeasureSegment(const char* text, uint16_t length, uint8_t flags);

    // Glyph-atlas cache for non-ASCII clusters: shaping, font fallback, and
    // rasterization run once per distinct cluster; every frame after that is
    // a plain blit. Monochrome clusters (colorGlyph=false) cache a
    // color-INDEPENDENT A8 coverage mask tinted at draw time via
    // FillOpacityMask — one raster serves every color, so rainbow art cannot
    // thrash the cache. Color-font clusters (emoji) cache a colored BGRA
    // raster keyed by (cluster, style, color) as before. Bitmaps are device
    // resources; cleared on font change, DPI change, and shutdown. Returns
    // null to request the uncached DrawText fallback (oversized key, frame
    // creation budget exhausted, or creation failure).
    ID2D1Bitmap1* GetClusterBitmap(const wchar_t* key, uint16_t len,
        uint8_t fmtIndex, uint8_t cells, uint32_t fgArgb, bool colorGlyph);
    void ClearClusterCache();

    IDWriteTextFormat* GetTextFormat(uint8_t flags) const;

private:
    // Presentation: child HWND + D3D11 flip-model swapchain. m_surface is the
    // swapchain's buffer-0 DXGI surface (flip model keeps index 0 writable).
    HWND                    m_hwnd = nullptr;
    ID3D11Device*           m_d3d11Device = nullptr;
    ID3D11DeviceContext*    m_d3dContext = nullptr;
    IDXGISwapChain1*        m_swapChain = nullptr;
    IDXGISurface*           m_surface = nullptr;

    // D2D device-context model: factory/device/context persist for the
    // renderer's lifetime so D2D's device-level glyph and rasterization
    // caches (color emoji in particular) and the brushes survive resizes;
    // only m_targetBitmap (the swapchain surface binding) swaps on resize.
    ID2D1Factory*           m_d2dFactory = nullptr;
    ID2D1Device*            m_d2dDevice = nullptr;
    ID2D1DeviceContext*     m_renderTarget = nullptr;
    ID2D1Bitmap1*           m_targetBitmap = nullptr;
    ID2D1SolidColorBrush*   m_defaultFgBrush = nullptr;
    ID2D1SolidColorBrush*   m_selectionBrush = nullptr;
    // Recolored per segment via SetColor; D2D snapshots brush state per call.
    ID2D1SolidColorBrush*   m_scratchBrush = nullptr;

    // DWrite
    IDWriteFactory*         m_dwriteFactory = nullptr;
    IDWriteTextFormat*      m_textFormats[4] = {}; // bit 0 bold, bit 1 italic
    IDWriteFontFace*        m_fontFace = nullptr;

    // Cluster atlas cache: direct-mapped, ~32 KB table + <=512 small device
    // bitmaps (A8 masks ~0.5 KB, colored emoji ~2 KB; bounded). Monochrome
    // entries exclude color from the key (tinted at draw time), so the slot
    // count only has to cover distinct GLYPHS, not glyph x color products.
    static constexpr uint32_t ClusterCacheSize = 512; // power of 2
    static constexpr uint16_t ClusterKeyMaxLen = 24;  // UTF-16 units; longer bypasses
    // Hard per-frame ceiling on atlas texture creations: a miss storm must
    // never become an unbounded GPU create/release stream (the driver's
    // deferred-destruction queue balloons by hundreds of MB). Overflow
    // renders through the per-frame DrawText fallback for that frame.
    static constexpr int AtlasCreateBudgetPerFrame = 256;
    struct ClusterEntry
    {
        wchar_t       key[ClusterKeyMaxLen];
        uint8_t       keyLen;  // 0 = empty slot
        uint8_t       fmt;     // format index (flags & 0x03)
        uint32_t      fgArgb;  // color entries: baked raster color (opaque);
                               // 0 marks a color-independent A8 mask entry
        ID2D1Bitmap1* bitmap;
    };
    ClusterEntry            m_clusterCache[ClusterCacheSize] = {};
    int                     m_atlasCreatesThisFrame = 0;
    // Second context on the same device for rendering atlas entries while
    // the main context is inside BeginDraw (nested draws on one context are
    // illegal; separate contexts on one device are fine and share resources).
    ID2D1DeviceContext*     m_atlasContext = nullptr;

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
    std::wstring            m_fontFamily = L"Consolas";
    float                   m_fontSize = 14.0f;
    uint32_t                m_fgColor = IrcPalette::DefaultFg; // default text color
    uint32_t                m_bgColor = IrcPalette::DefaultBg; // clear color
    uint32_t                m_selectionColor = IrcPalette::DefaultSelection; // highlight tint
    float                   m_charWidthDips = 8.0f;   // monospace advance width
    float                   m_underlineY = 15.4f;     // underline top, relative to row top
    float                   m_underlineThickness = 1.0f;
    float                   m_strikeY = 7.7f;         // strikethrough top, relative to row top
    float                   m_strikeThickness = 1.0f;
    float                   m_baselineY = 11.2f;      // uniform baseline for all DrawText chunks
    // CLIP always; ENABLE_COLOR_FONT OR'd in when the target supports it.
    D2D1_DRAW_TEXT_OPTIONS  m_drawTextOptions = D2D1_DRAW_TEXT_OPTIONS_CLIP;
    uint32_t                m_totalRows = 0;          // wrapped rows across all slots
    double                  m_scrollOffsetDips = 0.0; // distance from bottom of content; 0 = pinned
    bool                    m_autoScroll = true;
    bool                    m_dirty = true;
    bool                    m_fontDirty = false; // rebuild formats/rewrap next frame
    bool                    m_wrapDirty = false; // re-check columns next frame; rewrap if changed
    int                     m_lastWrapCols = 0;  // columns the row counts were computed at

    // Selection (drag in progress). Endpoints are absolute line ids
    // (RingBuffer::EvictedTotal() + logicalIndex) so they survive eviction,
    // plus byte offsets into the line (stable across rewrap).
    bool                    m_selectionActive = false;
    uint64_t                m_selAnchorLine = 0;
    uint16_t                m_selAnchorOffset = 0;
    uint64_t                m_selCaretLine = 0;
    uint16_t                m_selCaretOffset = 0;
    bool                    m_selWasAutoScroll = false; // pin state to restore on end

    // Batching
    uint64_t                m_lastInputTime = 0;

    // View-lifecycle coordination. The queue's only drainer is normally the
    // attached View's RenderFrame; with no attached View, producers drain
    // inline from AddLine so scrollback keeps accumulating while the host
    // control is unloaded. m_drainMutex serializes those drains (and ring
    // mutations from Clear/SetMaxLines) across the attach boundary;
    // m_viewAttached lets the attached hot path skip the lock entirely.
    std::mutex              m_drainMutex;
    std::atomic<bool>       m_viewAttached{ false };
    bool                    m_wrapExtended = true; // classic mod-16 fold of \x03 16-98;
                                                   // guarded by m_drainMutex (parse-time read)
    bool                    m_needsRewrap = false; // lines stored before the
                                                   // first View existed have
                                                   // rowCount 0; first attach
                                                   // runs RewrapAll
};

extern "C" __declspec(dllexport) Renderer* CreateRenderer();
extern "C" __declspec(dllexport) void DestroyRenderer(Renderer* renderer);
extern "C" __declspec(dllexport) bool AttachView(Renderer* renderer, HWND parent, int width, int height, float dpiScale);
extern "C" __declspec(dllexport) void DetachView(Renderer* renderer);
extern "C" __declspec(dllexport) HWND GetChildHwnd(Renderer* renderer);
extern "C" __declspec(dllexport) bool AddLine(Renderer* renderer, const char* text, int length);
extern "C" __declspec(dllexport) bool RenderFrame(Renderer* renderer, int* dirtyX, int* dirtyY, int* dirtyW, int* dirtyH);
extern "C" __declspec(dllexport) void SetSize(Renderer* renderer, int width, int height, float dpiScale);
extern "C" __declspec(dllexport) void ScrollByPixels(Renderer* renderer, float deltaDips);
extern "C" __declspec(dllexport) void ScrollToOffset(Renderer* renderer, float offsetDips);
extern "C" __declspec(dllexport) void ScrollToEnd(Renderer* renderer);
extern "C" __declspec(dllexport) void Clear(Renderer* renderer);
extern "C" __declspec(dllexport) void SetBackgroundColor(Renderer* renderer, uint32_t argb);
extern "C" __declspec(dllexport) void SetForegroundColor(Renderer* renderer, uint32_t argb);
extern "C" __declspec(dllexport) void SetSelectionColor(Renderer* renderer, uint32_t argb);
extern "C" __declspec(dllexport) void SetFontFamily(Renderer* renderer, const wchar_t* family);
extern "C" __declspec(dllexport) void SetFontSize(Renderer* renderer, float size);
extern "C" __declspec(dllexport) void SetMaxLines(Renderer* renderer, uint32_t maxLines);
extern "C" __declspec(dllexport) void SetExtendedColorWrap(Renderer* renderer, bool wrap);
extern "C" __declspec(dllexport) void ReleaseView(Renderer* renderer);
extern "C" __declspec(dllexport) void TrimStorage(Renderer* renderer);
extern "C" __declspec(dllexport) int GetLineCount(Renderer* renderer);
// "Chat" prefix avoids an extern "C" clash with the Win32 GetScrollInfo in winuser.h.
extern "C" __declspec(dllexport) void GetChatScrollInfo(Renderer* renderer, float* contentHeight,
    float* viewportHeight, float* scrollOffset, float* lineHeight, int* pinned);
extern "C" __declspec(dllexport) void SelectionBegin(Renderer* renderer, float xDips, float yDips);
extern "C" __declspec(dllexport) void SelectionUpdate(Renderer* renderer, float xDips, float yDips);
extern "C" __declspec(dllexport) int SelectionGetText(Renderer* renderer, char* buf, int cap);
extern "C" __declspec(dllexport) void SelectionEnd(Renderer* renderer);
