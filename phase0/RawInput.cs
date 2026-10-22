using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Mouser.Phase0;

/// <summary>
/// A hidden Win32 window that registers for raw mouse input with
/// RIDEV_INPUTSINK, so it sees deltas regardless of which app has focus.
///
/// This is the independent witness for Phase 0: if it goes silent while the gate
/// is engaged, we have proven the filter sits below raw input rather than merely
/// below the legacy message path.
///
/// Deliberately hand-rolled Win32 instead of WinForms: one hidden window should
/// not pull 130 MB of WPF into the deployment package.
/// </summary>
internal static class RawDump
{
    private const string ClassName = "MouserRawDumpWindow";

    private const int WmDestroy = 0x0002;
    private const int WmInput = 0x00FF;
    private const uint PmRemove = 0x0001;

    private const uint RidInput = 0x10000003;
    private const uint RidiDeviceName = 0x20000007;
    private const uint RidevInputSink = 0x00000100;
    private const uint RimTypeMouse = 0;

    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;

    private const ushort RiMouseMoveAbsolute = 0x0001;
    private const ushort RiMouseWheel = 0x0400;
    private const ushort RiMouseHWheel = 0x0800;
    private const ushort RiMouseButtonMask = 0x03FF;

    private static readonly WndProcDelegate WndProcThunk = WndProc;
    private static readonly byte[] Buffer = new byte[256];
    private static readonly Dictionary<IntPtr, string> Names = new();
    private static bool _nameWarningShown;

    public static int Run(Func<bool> isCancelled)
    {
        IntPtr instance = GetModuleHandleW(IntPtr.Zero);

        var windowClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcThunk),
            hInstance = instance,
            lpszClassName = ClassName,
        };

        if (RegisterClassExW(ref windowClass) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        IntPtr window = CreateWindowExW(
            WsExToolWindow,
            ClassName,
            "mouser-rawdump",
            WsPopup,
            0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

        if (window == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = 0x01,   // Generic Desktop
                usUsage = 0x02,       // Mouse
                dwFlags = RidevInputSink,
                hwndTarget = window,
            },
        };

        if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        Console.WriteLine("raw input dump - move the mouse, press Q or Ctrl+C to quit");
        Console.WriteLine("this is the independent witness: it must go silent while the gate is on");
        Console.WriteLine();

        while (!isCancelled())
        {
            while (PeekMessageW(out MSG message, IntPtr.Zero, 0, 0, PmRemove))
            {
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }

            if (!Console.IsInputRedirected)
            {
                try
                {
                    while (Console.KeyAvailable)
                    {
                        if (Console.ReadKey(intercept: true).Key == ConsoleKey.Q) return 0;
                    }
                }
                catch (InvalidOperationException)
                {
                    // No console attached; Ctrl+C is still handled by Program.
                }
            }

            Thread.Sleep(5);
        }

        return 0;
    }

    private static IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmInput)
        {
            Dump(lParam);
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static void Dump(IntPtr rawHandle)
    {
        uint size = (uint)Buffer.Length;
        int read = GetRawInputData(rawHandle, RidInput, Buffer, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
        if (read < 0) return;
        if (size < (uint)Marshal.SizeOf<RAWINPUTHEADER>()) return;

        RAWINPUTHEADER header = MemoryMarshal.Read<RAWINPUTHEADER>(Buffer);
        if (header.dwType != RimTypeMouse) return;

        RAWMOUSE mouse = MemoryMarshal.Read<RAWMOUSE>(Buffer.AsSpan(Marshal.SizeOf<RAWINPUTHEADER>()));

        var parts = new List<string>(4);

        if (mouse.lLastX != 0 || mouse.lLastY != 0)
        {
            // RDP and tablet devices report MOUSE_MOVE_ABSOLUTE, where lLastX/Y
            // are normalised screen coordinates rather than stroke deltas. That
            // distinction matters enormously later, so surface it here.
            string kind = (mouse.usFlags & RiMouseMoveAbsolute) != 0 ? "abs" : "rel";
            parts.Add($"{kind}=({mouse.lLastX,5},{mouse.lLastY,5})");
        }

        if ((mouse.usButtonFlags & RiMouseWheel) != 0)
        {
            parts.Add($"wheel={(short)mouse.usButtonData,3}");
        }

        if ((mouse.usButtonFlags & RiMouseHWheel) != 0)
        {
            parts.Add($"hwheel={(short)mouse.usButtonData,3}");
        }

        if ((mouse.usButtonFlags & RiMouseButtonMask) != 0)
        {
            parts.Add($"btn=0x{mouse.usButtonFlags:X4}");
        }

        if (parts.Count == 0) return;

        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {Describe(header.hDevice),-44}  {string.Join("  ", parts)}");
    }

    private static string Describe(IntPtr device)
    {
        if (Names.TryGetValue(device, out string? cached)) return cached;

        string label = $"dev:0x{device.ToInt64():X}";
        int byteCount = 4096 * sizeof(char);

        IntPtr memory = Marshal.AllocHGlobal(byteCount);
        try
        {
            // Zero first: if the call fails we must not read uninitialised heap.
            for (int i = 0; i < byteCount; i++) Marshal.WriteByte(memory, i, 0);

            uint size = 4096;
            uint written = GetRawInputDeviceInfoW(device, RidiDeviceName, memory, ref size);

            if (written > 0 && written != uint.MaxValue)
            {
                string? name = Marshal.PtrToStringUni(memory);
                if (!string.IsNullOrEmpty(name))
                {
                    if (name.StartsWith(@"\\?\", StringComparison.Ordinal)) name = name[4..];
                    label = name;
                }
            }
            else if (!_nameWarningShown)
            {
                _nameWarningShown = true;
                Console.WriteLine("note: device names unavailable on this system (seen on some RDP sessions)");
                Console.WriteLine("      falling back to raw device handles; the handle still identifies a device");
                Console.WriteLine();
            }

            Names[device] = label;
            return label;
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RAWMOUSE
    {
        [FieldOffset(0)] public ushort usFlags;
        [FieldOffset(4)] public ushort usButtonFlags;
        [FieldOffset(6)] public ushort usButtonData;
        [FieldOffset(8)] public uint ulRawButtons;
        [FieldOffset(12)] public int lLastX;
        [FieldOffset(16)] public int lLastY;
        [FieldOffset(20)] public uint ulExtraInformation;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(IntPtr moduleName);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW windowClass);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    private static extern bool PeekMessageW(out MSG message, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessageW(ref MSG message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetRawInputData(IntPtr rawInput, uint command, [Out] byte[] data, ref uint size, uint headerSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, IntPtr data, ref uint size);
}
