using System.Runtime.InteropServices;
using System.Text;

namespace Mouser.Phase0;

internal sealed record MouseInfo(int Device, string HardwareId);

/// <summary>
/// P/Invoke surface for the Interception filter driver
/// (https://github.com/oblitum/Interception).
///
/// The driver sits between the HID stack and mouclass, so strokes it does not
/// re-send never reach the OS. That includes raw-input consumers, which is the
/// whole reason we are not using a WH_MOUSE_LL hook.
/// </summary>
internal static class Interception
{
    public const string DllName = "interception.dll";

    public const int MaxKeyboard = 10;
    public const int MaxMouse = 10;

    public static int KeyboardDevice(int index) => index + 1;
    public static int MouseDevice(int index) => index + MaxKeyboard + 1;

    /// <summary>INTERCEPTION_FILTER_MOUSE_ALL</summary>
    public const ushort FilterMouseAll = 0xFFFF;

    /// <summary>INTERCEPTION_FILTER_NONE</summary>
    public const ushort FilterNone = 0x0000;

    /// <summary>INTERCEPTION_MOUSE_STROKE</summary>
    public const int StrokeMouse = 2;

    // InterceptionMouseFlag. These values are taken from interception.h; if
    // gating appears to do nothing, this is the first thing to re-check.
    public const ushort MouseLeftDown = 0x0001;
    public const ushort MouseLeftUp = 0x0002;
    public const ushort MouseRightDown = 0x0004;
    public const ushort MouseRightUp = 0x0008;
    public const ushort MouseMiddleDown = 0x0010;
    public const ushort MouseMiddleUp = 0x0020;
    public const ushort MouseButton4Down = 0x0040;
    public const ushort MouseButton4Up = 0x0080;
    public const ushort MouseButton5Down = 0x0100;
    public const ushort MouseButton5Up = 0x0200;
    public const ushort MouseWheel = 0x0400;
    public const ushort MouseHWheel = 0x0800;
    public const ushort MouseMove = 0x1000;
    public const ushort MouseVirtualDesktop = 0x2000;

    /// <summary>Strokes that carry relative motion (should be gated).</summary>
    public const ushort MotionMask = MouseMove;

    /// <summary>Strokes that carry scroll (should be gated).</summary>
    public const ushort WheelMask = MouseWheel | MouseHWheel;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MouseStroke
    {
        public ushort flags;
        public ushort rolling;
        public int x;
        public int y;
        public uint information;
    }

    /// <summary>
    /// InterceptionStroke is a packed union: a 4-byte type followed by either an
    /// 8-byte keyboard stroke or a 16-byte mouse stroke. Declaring the mouse
    /// member unconditionally makes sizeof == 20, matching the native struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Stroke
    {
        public int type;
        public MouseStroke mouse;
    }

    private static IntPtr _module;
    private static bool _loaded;

    public static bool TryPreload(out string error)
    {
        try
        {
            string local = Path.Combine(AppContext.BaseDirectory, DllName);
            _module = NativeLibrary.Load(File.Exists(local) ? local : DllName);
            _loaded = true;
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Address of the exported interception_is_mouse, passed to set_filter so the
    /// driver calls the DLL's own predicate instead of a managed trampoline.
    /// </summary>
    public static IntPtr MousePredicate
    {
        get
        {
            if (!_loaded) throw new InvalidOperationException("interception.dll is not loaded");
            return NativeLibrary.GetExport(_module, "interception_is_mouse");
        }
    }

    public static string GetHardwareId(IntPtr context, int device)
    {
        var buffer = new byte[1024];
        uint written = interception_get_hardware_id(context, device, buffer, (uint)buffer.Length);
        if (written == 0) return string.Empty;

        string text = Encoding.Unicode.GetString(buffer);
        int terminator = text.IndexOf('\0');
        return terminator >= 0 ? text[..terminator] : text;
    }

    public static List<MouseInfo> EnumerateMice(IntPtr context)
    {
        var result = new List<MouseInfo>(MaxMouse);
        for (int i = 0; i < MaxMouse; i++)
        {
            int device = MouseDevice(i);
            string id = GetHardwareId(context, device);
            if (id.Length != 0) result.Add(new MouseInfo(device, id));
        }
        return result;
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr interception_create_context();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void interception_destroy_context(IntPtr context);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int interception_wait_with_timeout(IntPtr context, uint milliseconds);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int interception_receive(IntPtr context, int device, [Out] Stroke[] strokes, uint nstroke);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int interception_send(IntPtr context, int device, Stroke[] strokes, uint nstroke);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int interception_set_filter(IntPtr context, ushort filter, IntPtr predicate);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int interception_is_mouse(int device);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int interception_is_keyboard(int device);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint interception_get_hardware_id(IntPtr context, int device, [Out] byte[] buffer, uint size);
}
