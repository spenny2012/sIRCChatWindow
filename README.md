# High-Performance Native IRC Chat Control for WPF

A console-style chat control that bypasses WPF's retained-mode text stack by rendering IRC content in a native C++ DLL with Direct2D / DirectWrite, hosted inside a WPF `D3DImage` via a shared DirectX surface.

## Goals Met

- **Throughput:** lock-free MPSC input queue + fixed-size ring buffer allow the producer thread to push 1,000–5,000+ lines/sec without blocking the render or UI threads.
- **Scrollback:** 50,000-line pre-allocated ring buffer (no heap allocations on the hot path).
- **Frame time:** only visible lines are drawn; a 60 FPS `DispatcherTimer` drives invalidation.
- **Memory:** ring buffer is ~32 MB fixed cost regardless of churn.
- **No WPF text objects:** `FlowDocument`, `Run`, `Span`, etc. are not used for chat content.

## Solution Layout

```
IrcChat.sln
├── IrcRendererNative/          C++ native DLL
│   ├── RingBuffer.h/cpp        Fixed-capacity LineSlot ring buffer
│   ├── MpscQueue.h             Lock-free multi-producer / single-consumer queue
│   ├── IrcParser.h/cpp         Inline mIRC control-code parser
│   ├── Renderer.h/cpp          D3D11 shared texture, D2D/DWrite renderer, C exports
│   └── Exports.cpp             Export forwarding
└── IrcChatWpf/                 .NET 8 WPF test application
    ├── NativeMethods.cs        P/Invoke + D3D9Ex COM interop
    ├── IrcD3DImage.cs          D3DImage-derived host
    ├── IrcChatControl.xaml/.cs WPF wrapper + scrollbar
    └── MainWindow.xaml/.cs     High-throughput spam test harness
```

## Build Dependencies

- Windows 10/11 64-bit
- Visual Studio 2022 with the **Desktop development with C++** workload
- **Windows 10/11 SDK** (10.0 or later)
- **.NET 8 SDK**
- VC++ redistributable matching the VS toolset (v143 / VC17) when running on another machine

## Build Instructions

1. Open `IrcChat.sln` in Visual Studio 2022.
2. Select **Release | x64**.
3. Build `IrcRendererNative` first. The DLL is written to `build\Release\IrcRendererNative.dll`.
4. Build `IrcChatWpf`. The project copies `IrcRendererNative.dll` to its output directory.
5. Run `IrcChatWpf.exe`.

Or from the command line:

```powershell
# Build the native DLL
msbuild IrcChat.sln -p:Configuration=Release -p:Platform=x64 -t:IrcRendererNative

# Build the WPF app
dotnet build IrcChatWpf\IrcChatWpf.csproj -c Release -p:Platform=x64
```

## Test Harness

The main window contains:

- **Start Spam** – launches a background thread that injects random IRC-formatted lines at the configured rate.
- **Rate** – target lines per second (default 2,000).
- **Stop Spam** – stops the producer thread.
- **Clear** – empties the native ring buffer.

Interact with the chat:

- **Mouse wheel** – pixel-smooth scrolling; honors the OS "lines per notch" setting and high-resolution wheel deltas.
- **Page Up / Page Down** – scroll by one viewport.
- **Home / End** – jump to the oldest / newest line.
- **ScrollBar** – operates in pixel (DIP) units against the native content extent; dragging the thumb scrubs live.

## Native C Export Contract

`IrcRendererNative.dll` exposes plain C-style exports:

```c
Renderer* CreateRenderer(int widthPx, int heightPx, float dpiScale,
                         int surfaceWidthPx, int surfaceHeightPx);
void      DestroyRenderer(Renderer* renderer);
bool      AddLine(Renderer* renderer, const char* text, int length);
bool      RenderFrame(Renderer* renderer, int* dirtyX, int* dirtyY, int* dirtyW, int* dirtyH);
HANDLE    GetSharedHandle(Renderer* renderer);
void      SetSize(Renderer* renderer, int widthPx, int heightPx, float dpiScale);   // viewport only, no realloc
bool      ResizeSurface(Renderer* renderer, int surfaceWidthPx, int surfaceHeightPx); // grow-only, rebind back buffer after
void      ScrollByPixels(Renderer* renderer, float deltaDips);   // + = up / into history
void      ScrollToOffset(Renderer* renderer, float offsetDips);  // absolute distance from bottom, clamped
void      ScrollToEnd(Renderer* renderer);
void      Clear(Renderer* renderer);
int       GetLineCount(Renderer* renderer);
void      GetChatScrollInfo(Renderer* renderer, float* contentHeight, float* viewportHeight,
                            float* scrollOffset, float* lineHeight, int* pinned);
```

Surface dimensions are device pixels; all scroll/layout values are DIPs. The scroll
offset is the distance from the bottom of content to the bottom of the viewport
(`0` = pinned to the newest line, auto-scroll engaged). While scrolled up, appended
lines grow the offset so the visible text stays stationary.

All calls are single-threaded except `AddLine`, which is thread-safe via the lock-free MPSC queue.

## Architecture Notes

### Zero-Allocation Hot Path

- `AddLine` copies UTF-8 bytes into a lock-free circular queue (`MpscQueue`) using only atomic operations.
- The render thread dequeues lines during `RenderFrame` and parses them directly into the next `LineSlot` of the pre-allocated ring buffer.
- No `std::string`, `std::vector`, or exceptions are used in the network/render hot paths.

### Ring Buffer

- Capacity: 50,000 lines (`IrcLineCapacity` in `RingBuffer.h`).
- Each slot holds 512 bytes of text + 16 formatting segments + metadata.
- When full, the oldest slot is overwritten; `head`, `tail`, and `count` are atomic.

### Rendering

- Native side creates a D3D11 BGRA render-target texture with `D3D11_RESOURCE_MISC_SHARED`.
- WPF creates a D3D9Ex device and opens the shared texture by handle for `D3DImage.SetBackBuffer`.
- Direct2D renders into the D3D11 texture; DirectWrite draws each colored segment with a cached `IDWriteTextFormat`.
- Only the visible viewport is rendered (from the first visible row until `y` passes the viewport height, with sub-line pixel offsets for smooth scrolling).
- Long lines word-wrap at spaces onto continuation rows with a 2-character hanging indent. Wrapping is column arithmetic (exact for the monospace font): each slot caches its wrapped row count, the renderer keeps a running total for scroll extent, and a resize re-wraps all slots in one O(total chars) pass.
- The surface chain is allocated once at screen size; the renderer draws into the top-left viewport region and WPF shows it 1:1 (`Stretch="None"` + clip). Interactive resizes therefore reflow live without reallocating GPU resources or scaling glyphs; the surface only grows (via `ResizeSurface`) if the viewport outgrows it, e.g. moving to a larger monitor.
- After `EndDraw`, the renderer flushes the D3D11 context and waits on an event query before returning. Without this, WPF's D3D9 side copies the shared surface while the frame is still executing on the GPU, which shows up as a half-drawn frame: text at the top, blank clear color at the bottom.

### Formatting

mIRC control codes parsed inline:

| Code | Meaning |
|------|---------|
| `\x02` | Toggle bold |
| `\x03` | Foreground color, optional `,background` (0–15) |
| `\x0F` | Reset all formatting |
| `\x1F` | Toggle underline |
| `\x16` | Toggle italic |

## Tuning

| Constant | Location | Effect |
|----------|----------|--------|
| `IrcLineCapacity` | `RingBuffer.h` | Scrollback line count. Changing it scales the fixed memory cost linearly. |
| `IrcLineTextSize` | `RingBuffer.h` | Maximum bytes per line. |
| `IrcMaxSegments` | `RingBuffer.h` | Maximum color/format runs per line. |
| `InputQueueCapacity` | `Renderer.h` | Lock-free queue depth for bursty producers. Must be a power of two. |
| `MaxInputBatch` | `Renderer.cpp` | Lines drained from the queue per frame. Increase for > ~15k lines/sec. |
| Timer interval | `IrcD3DImage.cs` | Default 16 ms (~60 FPS). Lower for lower latency, raise to reduce CPU. |
| `ContinuationIndentChars` | `Renderer.cpp` | Indent (in characters) for wrapped continuation rows. |
| `FontSize` / `FontFamily` | `Renderer.cpp` | Default Consolas 14 pt. |

## Known Limitations / Future Work

- **Dirty rectangles** currently mark the full surface each frame. New-line-only and scroll-bit-blit optimizations are straightforward additions.
- **Unicode:** text is currently treated as byte-per-character for rendering. Full UTF-8 support would store UTF-16 segment offsets and use `DrawText` with UTF-16 buffers.
- **Text selection / copy:** not implemented; would require hit-testing against segment metrics.
- **Underline / italic rendering:** uses separate cached `IDWriteTextFormat` objects; no per-line `IDWriteTextLayout` is created.

## License

This is a reference / starting-point implementation provided as-is for integration into a larger application.
