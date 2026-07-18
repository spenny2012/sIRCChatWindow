# IrcChatControl.Wpf

A high-throughput, console-style IRC chat control for WPF. Text is rendered by a native C++ DLL with Direct2D / DirectWrite into a D3D11 flip-model swapchain on a child HWND (hosted via `HwndHost`), bypassing WPF's retained-mode text stack entirely. A lock-free MPSC input queue and a fixed pre-allocated ring buffer let producer threads push thousands of lines per second without blocking the UI.

- **Throughput:** 1,000–5,000+ lines/sec sustained; `AddLine` is thread-safe and allocation-free on the hot path.
- **Scrollback:** 50,000-line pre-allocated ring buffer (configurable cap at runtime via `SetMaxLines`).
- **Formatting:** mIRC control codes (bold, italic, underline, colors incl. extended range) and ANSI SGR escape sequences (`ESC[...m`, incl. 38/48 extended colors).
- **Unicode:** full UTF-8 pipeline with emoji (color font) and CJK wide-cell support.
- **Interaction:** pixel-smooth wheel scrolling, PageUp/PageDown/Home/End, live scrollbar scrubbing, drag selection with clipboard copy, Ctrl+wheel font zoom.
- **Idle cost:** the render timer parks when nothing changes — 0% CPU at idle.

## Requirements

- Windows 10/11, **x64 only** (see note below)
- .NET 8 (`net8.0-windows`)
- No VC++ redistributable needed — the native renderer links the CRT statically

> **x64 note:** the native renderer ships only as a `win-x64` binary. Set `<PlatformTarget>x64</PlatformTarget>` in your app project. On Windows ARM64 an AnyCPU .NET 8 app runs as a native arm64 process and *will not* load the x64 DLL (`win-arm64` does not fall back to `win-x64` in the RID graph); forcing `PlatformTarget=x64` makes the app run under x64 emulation instead, which works.

## Install (NuGet)

```
dotnet add package IrcChatControl.Wpf
```

XAML:

```xml
<Window ...
        xmlns:chat="clr-namespace:IrcChatWpf;assembly=IrcChatControl.Wpf">
    <chat:IrcChatControl x:Name="Chat" />
</Window>
```

Code:

```csharp
Chat.AddLine("\x0304Hello \x02world\x02 — colors, bold, ANSI \x1b[32mgreen\x1b[0m");
```

## Use as a git submodule

```
git submodule add https://github.com/spenny2012/sIRCChatWindow.git external/sIRCChatWindow
```

1. Add `src/IrcRendererNative/IrcRendererNative.vcxproj` and `src/IrcChatControl.Wpf/IrcChatControl.Wpf.csproj` to your solution (the native project needs the VS "Desktop development with C++" workload + Windows 10/11 SDK).
2. Set a solution build dependency: **IrcChatControl.Wpf → IrcRendererNative** (the native DLL must exist before the library builds; you'll get a clear build warning if it doesn't).
3. Reference `IrcChatControl.Wpf` from your app with a `ProjectReference`. The native DLL flows to your output automatically.

The native project always writes to `<submodule>/build/<Configuration>/` regardless of where your solution lives, and the library looks it up there — no path wiring needed.

## API

| Member | Description |
|--------|-------------|
| `AddLine(string text)` | Append a line (thread-safe, lock-free). mIRC + ANSI codes parsed inline. |
| `Clear()` | Empty the scrollback (also decommits the arena). |
| `LineCount` | Lines currently held in the ring buffer. |
| `SetMaxLines(int)` | Cap the scrollback line count at runtime. |
| `SetBackgroundColor(Color)` / `SetForegroundColor(Color)` / `SetSelectionColor(Color)` | Theme the control. |
| `SetFontFamily(string)` / `SetFontSize(double)` | Font control (default Consolas 14). |
| `EnableFontZoom` | Enable/disable Ctrl+wheel zoom (default on, clamped 6–72 pt). |
| `CurrentFontSize` | The effective font size after zooming. |

Keyboard scrolling (PageUp/PageDown/Home/End) activates after the control has been clicked (it takes focus on mouse-down).

### Formatting codes

| Code | Meaning |
|------|---------|
| `\x02` | Toggle bold |
| `\x03` | mIRC foreground color, optional `,background` |
| `\x0F` | Reset all formatting |
| `\x1F` | Toggle underline |
| `\x16` | Toggle italic |
| `\x1B[...m` | ANSI SGR: styles, 16/256/truecolor via 38/48 |

## Building from source

```powershell
# 1. Native renderer (needs VS 2022 C++ workload + Windows SDK)
msbuild src\IrcRendererNative\IrcRendererNative.vcxproj -p:Configuration=Release -p:Platform=x64

# 2. Demo app (also builds the library)
dotnet build demo\IrcChatWpf\IrcChatWpf.csproj -c Release -p:Platform=x64

# Run the spam-test harness
demo\IrcChatWpf\bin\x64\Release\net8.0-windows\win-x64\IrcChatWpf.exe
```

Or open `IrcChat.sln` in Visual Studio 2022 (Release | x64) — build order is wired in the solution.

The demo harness generates random formatted lines at a configurable rate (default 2,000/sec) with buttons for theming, font cycling, scrollback capping, and a test pattern.

## Architecture

```
IrcChat.sln
├── src/IrcRendererNative/       C++ native DLL (Direct2D/DirectWrite/D3D11)
│   ├── RingBuffer.h/cpp         Fixed-capacity line ring buffer (arena-backed)
│   ├── MpscQueue.h              Lock-free multi-producer/single-consumer queue
│   ├── IrcParser.h/cpp          Inline mIRC + ANSI SGR parser
│   ├── TextCells.h              Emoji/CJK cell model
│   └── Renderer.h/cpp           Flip-model swapchain, D2D/DWrite rendering
├── src/IrcChatControl.Wpf/      .NET 8 WPF class library (the NuGet package)
│   ├── NativeMethods.cs         P/Invoke layer
│   ├── IrcSwapchainHost.cs      HwndHost hosting the renderer's child HWND
│   └── IrcChatControl.xaml/.cs  Public UserControl + scrollbar/input handling
└── demo/IrcChatWpf/             Spam-test harness (WinExe)
```

- `AddLine` UTF-8-encodes straight into the lock-free queue; the render pass drains it into pre-allocated ring-buffer slots — no `std::string`, no heap allocation, no exceptions on the hot path.
- Only visible lines are drawn. A ~60 Hz `DispatcherTimer` drives frames and **parks itself when idle** (a wake protocol on the producer side restarts it), so an idle window costs zero CPU.
- Word-wrap is column arithmetic against the monospace grid; each slot caches its wrapped row count and resizes re-wrap in one pass (deferred while zooming).
- Rendering goes to a DXGI flip-model swapchain on a child HWND — no D3D9 interop, no `D3DImage`, no per-frame GPU copies through WPF.

## Host-app memory tuning (optional)

The demo's [`App.xaml.cs`](demo/IrcChatWpf/App.xaml.cs) shows two optional, app-level optimizations that meaningfully shrink a chat-centric app's footprint; both are choices for *your* application, not behavior of the control:

- `RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly` — keeps WPF itself off the GPU. The chat surface has its own swapchain, so WPF hardware acceleration would only composite chrome, at the cost of a D3D9Ex device and vendor driver stack (~tens of MB). Set it before the first window is created; skip it if your app has GPU-heavy WPF UI of its own.
- A settle-then-trim pattern (aggressive Gen2 collect + `SetProcessWorkingSetSize` once at startup and again after activity bursts go quiet) — startup XAML parsing and message floods leave garbage in committed GC regions that steady state never touches.

## Tuning constants

| Constant | Location | Effect |
|----------|----------|--------|
| `IrcLineCapacity` | `RingBuffer.h` | Compile-time scrollback capacity (50,000). |
| `IrcLineTextSize` | `RingBuffer.h` | Max bytes per line (512). |
| `IrcMaxSegments` | `RingBuffer.h` | Max color/format runs per line (16). |
| `InputQueueCapacity` | `Renderer.h` | Lock-free queue depth (power of two). |
| `MaxInputBatch` | `Renderer.cpp` | Lines drained per frame; raise for >15k lines/sec. |
| Timer interval | `IrcSwapchainHost.cs` | ~16 ms (60 FPS); parks at idle. |

## License

GPL-3.0-or-later — see [LICENSE](LICENSE). Note the copyleft implication: if you distribute an application that includes this control, the GPL's terms apply to that distribution. (© the repository owner; other arrangements are possible on request.)
