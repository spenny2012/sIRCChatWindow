using System;
using System.Runtime.InteropServices;
using System.Security;

namespace IrcChatWpf
{
    [SuppressUnmanagedCodeSecurity]
    internal static class NativeMethods
    {
        private const string DllName = "IrcRendererNative.dll";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr CreateRenderer(IntPtr parentHwnd, int widthPx, int heightPx, float dpiScale);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr GetChildHwnd(IntPtr renderer);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void DestroyRenderer(IntPtr renderer);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool AddLine(IntPtr renderer, ref byte text, int length);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool RenderFrame(IntPtr renderer, out int dirtyX, out int dirtyY, out int dirtyW, out int dirtyH);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetSize(IntPtr renderer, int widthPx, int heightPx, float dpiScale);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ScrollByPixels(IntPtr renderer, float deltaDips);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ScrollToOffset(IntPtr renderer, float offsetDips);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ScrollToEnd(IntPtr renderer);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void Clear(IntPtr renderer);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetBackgroundColor(IntPtr renderer, uint argb);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetForegroundColor(IntPtr renderer, uint argb);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetSelectionColor(IntPtr renderer, uint argb);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern void SetFontFamily(IntPtr renderer, [MarshalAs(UnmanagedType.LPWStr)] string family);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetFontSize(IntPtr renderer, float size);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetLineCount(IntPtr renderer);

        [DllImport(DllName, EntryPoint = "GetChatScrollInfo", CallingConvention = CallingConvention.Cdecl)]
        public static extern void GetScrollInfo(IntPtr renderer, out float contentHeight,
            out float viewportHeight, out float scrollOffset, out float lineHeight, out int pinned);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SelectionBegin(IntPtr renderer, float xDips, float yDips);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SelectionUpdate(IntPtr renderer, float xDips, float yDips);

        // buf == null: returns the required UTF-8 byte count (0 = no selection).
        // buf != null: writes up to cap bytes, returns the bytes written.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SelectionGetText(IntPtr renderer, byte[] buf, int cap);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SelectionEnd(IntPtr renderer);
    }
}
