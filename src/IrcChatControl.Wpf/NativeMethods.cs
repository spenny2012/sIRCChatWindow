using System;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32.SafeHandles;

namespace IrcChatWpf
{
    /// <summary>Owns a native Renderer* for the lifetime of an
    /// <see cref="IrcChatControl"/>. WPF controls have no disposal hook, so
    /// the SafeHandle finalizer is what ultimately frees the native document
    /// (ring buffer + arena) after the control is discarded; SafeHandle
    /// marshaling also keeps producer-thread AddLine calls safe against
    /// teardown races.</summary>
    internal sealed class SafeRendererHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeRendererHandle() : base(true) { }

        protected override bool ReleaseHandle()
        {
            NativeMethods.DestroyRenderer(handle);
            return true;
        }
    }

    [SuppressUnmanagedCodeSecurity]
    internal static class NativeMethods
    {
        private const string DllName = "IrcRendererNative.dll";

        // Allocates the persistent document (ring buffer + input queue) only;
        // the View (HWND + GPU stack) attaches separately.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern SafeRendererHandle CreateRenderer();

        // IntPtr overload exists solely for SafeRendererHandle.ReleaseHandle.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void DestroyRenderer(IntPtr renderer);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool AttachView(SafeRendererHandle renderer, IntPtr parentHwnd, int widthPx, int heightPx, float dpiScale);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void DetachView(SafeRendererHandle renderer);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr GetChildHwnd(SafeRendererHandle renderer);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool AddLine(SafeRendererHandle renderer, ref byte text, int length);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool RenderFrame(SafeRendererHandle renderer, out int dirtyX, out int dirtyY, out int dirtyW, out int dirtyH);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetSize(SafeRendererHandle renderer, int widthPx, int heightPx, float dpiScale);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ScrollByPixels(SafeRendererHandle renderer, float deltaDips);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ScrollToOffset(SafeRendererHandle renderer, float offsetDips);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ScrollToEnd(SafeRendererHandle renderer);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void Clear(SafeRendererHandle renderer);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetBackgroundColor(SafeRendererHandle renderer, uint argb);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetForegroundColor(SafeRendererHandle renderer, uint argb);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetSelectionColor(SafeRendererHandle renderer, uint argb);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern void SetFontFamily(SafeRendererHandle renderer, [MarshalAs(UnmanagedType.LPWStr)] string family);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetFontSize(SafeRendererHandle renderer, float size);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetMaxLines(SafeRendererHandle renderer, uint maxLines);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetExtendedColorWrap(SafeRendererHandle renderer, [MarshalAs(UnmanagedType.U1)] bool wrap);

        // Destroys a PARKED view's HWND/GPU stack (no-op while attached);
        // scrollback and theme state survive. Used by the LRU park limit.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ReleaseView(SafeRendererHandle renderer);

        // Returns committed scrollback bytes no longer in use to the OS
        // (arena compaction + decommit). Idle-time call; scrollback intact.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void TrimStorage(SafeRendererHandle renderer);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetLineCount(SafeRendererHandle renderer);

        [DllImport(DllName, EntryPoint = "GetChatScrollInfo", CallingConvention = CallingConvention.Cdecl)]
        public static extern void GetScrollInfo(SafeRendererHandle renderer, out float contentHeight,
            out float viewportHeight, out float scrollOffset, out float lineHeight, out int pinned);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SelectionBegin(SafeRendererHandle renderer, float xDips, float yDips);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SelectionUpdate(SafeRendererHandle renderer, float xDips, float yDips);

        // buf == null: returns the required UTF-8 byte count (0 = no selection).
        // buf != null: writes up to cap bytes, returns the bytes written.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SelectionGetText(SafeRendererHandle renderer, byte[] buf, int cap);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SelectionEnd(SafeRendererHandle renderer);
    }
}
