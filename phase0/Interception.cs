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
///
/// Signatures here were verified against library/interception.h and
/// library/interception.c, not inferred. Two details are easy to get wrong and
/// both are fatal:
///
///   * interception_set_filter takes (context, predicate, filter). Passing the
///     filter where the predicate belongs makes the DLL call the filter value as
///     a function pointer and the process dies with an AccessViolation.
///   * InterceptionStroke is char[20] with no type tag. InterceptionMouseStroke
///     begins with `state`, not `flags`, and has two bytes of padding before `x`.
/// </summary>
internal static class Interception
{
    public const string DllName = "interception.dll";

    public const int MaxKeyboard = 10;
    public const int MaxMouse = 10;

    public static int KeyboardDevice(int index) => index + 1;
    public static int MouseDevice(int index) => index + MaxKeyboard + 1;

    /// <summary>sizeof(InterceptionMouseStroke), which is also sizeof(InterceptionStroke).</summary>
    public const int StrokeSize = 20;

    /// <summary>INTERCEPTION_FILTER_MOUSE_ALL</summary>
    public const ushort FilterMouseAll = 0xFFFF;

    /// <summary>INTERCEPTION_FILTER_MOUSE_NONE</summary>
    public const ushort FilterNone = 0x0000;

    // InterceptionMouseState: these live in MouseStroke.state.
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

    /// <summary>Everything that is a button, i.e. not wheel and not motion.</summary>
    public const ushort ButtonMask = 0x03FF;

    public const ushort WheelMask = MouseWheel | MouseHWheel;

    // InterceptionMouseFlag: these live in MouseStroke.flags. Note that 0x1000 is
    // INTERCEPTION_FILTER_MOUSE_MOVE and appears in the *filter* word only; it is
    // never set in a stroke's flags. Motion is identified by state == 0.
    public const ushort FlagMoveAbsolute = 0x0001;
    public const ushort FlagVirtualDesktop = 0x0002;
    public const ushort FlagAttributesChanged = 0x0004;
    public const ushort FlagMoveNoCoalesce = 0x0008;
    public const ushort FlagTermsrvSrcShadow = 0x0100;

    /// <summary>
    /// Mirrors InterceptionMouseStroke field for field, including the padding
    /// between `rolling` and `x`. Explicit layout so a future runtime or compiler
    /// change cannot silently alter the stride.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = StrokeSize)]
    public struct MouseStroke
    {
        /// <summary>Button and wheel bits.</summary>
        [FieldOffset(0)] public ushort state;

        /// <summary>MOUSE_MOVE_ABSOLUTE and friends.</summary>
        [FieldOffset(2)] public ushort flags;

        /// <summary>Signed wheel delta.</summary>
        [FieldOffset(4)] public short rolling;

        [FieldOffset(8)] public int x;

        [FieldOffset(12)] public int y;

        [FieldOffset(16)] public uint information;
    }

    private static IntPtr _module;
    private static bool _loaded;

    public static bool TryPreload(out string error)
    {
        try
        {
            if (Marshal.SizeOf<MouseStroke>() != StrokeSize)
            {
                error = $"internal error: MouseStroke marshals to {Marshal.SizeOf<MouseStroke>()} bytes, expected {StrokeSize}";
                return false;
            }

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
    /// Address of the exported interception_is_mouse, passed to set_filter. The
    /// driver library calls this once per device slot, so it must be a real native
    /// function pointer and not a managed delegate.
    /// </summary>
    public static IntPtr MousePredicate
    {
        get
        {
            if (!_loaded) throw new InvalidOperationException("interception.dll is not loaded");
            return NativeLibrary.GetExport(_module, "interception_is_mouse");
        }
    }

    /// <summary>
    /// Sets the delivery filter for every device the predicate accepts.
    /// Argument order is (context, predicate, filter) - see the class remarks.
    /// </summary>
    public static void SetMouseFilter(IntPtr context, ushort filter)
        => interception_set_filter(context, MousePredicate, filter);

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
    public static extern int interception_receive(
        IntPtr context, int device,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] MouseStroke[] strokes,
        uint nstroke);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int interception_send(
        IntPtr context, int device,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] MouseStroke[] strokes,
        uint nstroke);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void interception_set_filter(IntPtr context, IntPtr predicate, ushort filter);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int interception_is_mouse(int device);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint interception_get_hardware_id(IntPtr context, int device, [Out] byte[] buffer, uint size);
}
