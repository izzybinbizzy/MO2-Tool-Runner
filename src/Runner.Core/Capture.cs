using System.Runtime.InteropServices;

namespace ToolRunner;

// A picture of one window drawn by the window itself (PrintWindow) - it works while the window is behind others, so nothing
// is ever brought to the front to read it.
public sealed class Picture(int a_width, int a_height, int[] a_pixels)
{
	public int Width { get; } = a_width;
	public int Height { get; } = a_height;
	readonly int[] _px = a_pixels;

	public (int R, int G, int B) At(int a_x, int a_y)
	{
		var v = _px[a_y * Width + a_x];
		return ((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
	}

	public bool Blank => _px.Distinct().Take(2).Count() < 2;
}

public static partial class Capture
{
	[LibraryImport("user32.dll")]
	private static partial nint GetDC(nint a_hwnd);

	[LibraryImport("user32.dll")]
	private static partial int ReleaseDC(nint a_hwnd, nint a_dc);

	[LibraryImport("gdi32.dll")]
	private static partial nint CreateCompatibleDC(nint a_dc);

	[LibraryImport("gdi32.dll")]
	private static partial nint CreateCompatibleBitmap(nint a_dc, int a_w, int a_h);

	[LibraryImport("gdi32.dll")]
	private static partial nint SelectObject(nint a_dc, nint a_obj);

	[LibraryImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool DeleteObject(nint a_obj);

	[LibraryImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool DeleteDC(nint a_dc);

	[StructLayout(LayoutKind.Sequential)]
	struct BITMAPINFOHEADER
	{
		public uint Size;
		public int Width, Height;
		public ushort Planes, BitCount;
		public uint Compression, SizeImage;
		public int XPels, YPels;
		public uint ClrUsed, ClrImportant;
	}

	[DllImport("gdi32.dll")]
	static extern int GetDIBits(nint a_dc, nint a_bmp, uint a_start, uint a_lines, [Out] int[] a_bits, ref BITMAPINFOHEADER a_info, uint a_usage);

	const uint PW_CLIENTONLY = 1, PW_RENDERFULLCONTENT = 2;

	// the client area of a window, or null when it cannot be drawn
	public static Picture? Client(nint a_hwnd)
	{
		if (!Native.GetClientRect(a_hwnd, out var r) || r.Width <= 0 || r.Height <= 0) {
			return null;
		}
		var screen = GetDC(0);
		var dc = screen == 0 ? 0 : CreateCompatibleDC(screen);
		var bmp = dc == 0 ? 0 : CreateCompatibleBitmap(screen, r.Width, r.Height);
		var old = bmp == 0 ? 0 : SelectObject(dc, bmp);
		try {
			if (bmp == 0 || !Native.PrintWindow(a_hwnd, dc, PW_CLIENTONLY | PW_RENDERFULLCONTENT)) {
				return null;
			}
			SelectObject(dc, old);
			old = 0;
			var info = new BITMAPINFOHEADER { Size = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), Width = r.Width, Height = -r.Height, Planes = 1, BitCount = 32 };
			var px = new int[r.Width * r.Height];
			return GetDIBits(dc, bmp, 0, (uint)r.Height, px, ref info, 0) == r.Height ? new Picture(r.Width, r.Height, px) : null;
		} finally {
			// a bitmap still selected into its DC cannot be deleted: deselected first (else each failed capture leaks one)
			if (old != 0) {
				SelectObject(dc, old);
			}
			if (bmp != 0) {
				DeleteObject(bmp);
			}
			if (dc != 0) {
				DeleteDC(dc);
			}
			if (screen != 0) {
				_ = ReleaseDC(0, screen);
			}
		}
	}
}
