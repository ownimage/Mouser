using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Mouser.Phase0;

internal sealed class OptionException : Exception
{
    public OptionException(string message) : base(message) { }
}

internal sealed class Options
{
    public string Mode { get; set; } = string.Empty;
    public string? Filter { get; set; }
    public IPEndPoint? UdpTarget { get; set; }
    public int Port { get; set; } = 7801;
    public int HoldButton { get; set; }

    public static Options Parse(string[] args)
    {
        var options = new Options();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--list": options.Mode = "list"; break;
                case "--passthrough": options.Mode = "passthrough"; break;
                case "--gate": options.Mode = "gate"; break;
                case "--rawdump": options.Mode = "rawdump"; break;
                case "--recv": options.Mode = "recv"; break;
                case "--ping": options.Mode = "ping"; break;
                case "-h":
                case "--help": options.Mode = "help"; break;

                case "--filter":
                    options.Filter = Value(args, ref i, arg);
                    break;

                case "--udp":
                    options.UdpTarget = ParseEndpoint(Value(args, ref i, arg));
                    break;

                case "--port":
                    options.Port = ParseInt(Value(args, ref i, arg), arg);
                    break;

                case "--hold":
                    options.HoldButton = ParseInt(Value(args, ref i, arg), arg);
                    if (options.HoldButton is not (0 or 3 or 4 or 5))
                    {
                        throw new OptionException("--hold must be 0 (off), 3, 4 or 5");
                    }
                    break;

                default:
                    throw new OptionException($"unknown argument '{arg}'");
            }
        }

        return options;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            mouser phase0 - read a mouse below raw input and stop it propagating

            usage:
              mouser-phase0 --list
              mouser-phase0 --passthrough [--filter <text>]
              mouser-phase0 --gate        [--filter <text>] [--hold 3|4|5] [--udp host:port]
              mouser-phase0 --rawdump
              mouser-phase0 --recv        [--port 7801]
              mouser-phase0 --ping        --udp host:port

            modes:
              --list         show mouse/keyboard hardware ids as seen by the driver
              --passthrough  re-send every stroke unchanged; the mouse must feel normal
              --gate         drop motion and scroll while gated, forward everything else
              --rawdump      independent raw-input reader; proves we sit below raw input
              --recv         UDP receiver for telemetry and ping echo
              --ping         round-trip probe against --recv

            options:
              --filter <t>   only manage devices whose hardware id contains <t> (case-insensitive)
              --hold <n>     momentary gate on mouse button n while --gate is active
                             (default 0: G key only)
              --udp <h:p>    send 32-byte telemetry frames at ~100 Hz to <h:p>
              --port <n>     UDP port for --recv (default 7801)

            keys:
              G  toggle gate      Q  quit      Ctrl+C  quit

            Suggested order: --list, then --passthrough, then --gate --filter <your mouse>.
            """);
    }

    private static string Value(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length) throw new OptionException($"{name} needs a value");
        return args[++index];
    }

    private static int ParseInt(string text, string name)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw new OptionException($"{name}: '{text}' is not a number");
        }
        return value;
    }

    private static IPEndPoint ParseEndpoint(string text)
    {
        if (IPEndPoint.TryParse(text, out IPEndPoint? endpoint)) return endpoint;

        int colon = text.LastIndexOf(':');
        if (colon <= 0) throw new OptionException($"--udp expects host:port, got '{text}'");

        string host = text[..colon];
        int port = ParseInt(text[(colon + 1)..], "--udp");

        try
        {
            foreach (IPAddress address in Dns.GetHostAddresses(host))
            {
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    return new IPEndPoint(address, port);
                }
            }
        }
        catch (SocketException)
        {
            // reported below
        }

        throw new OptionException($"--udp: could not resolve '{host}'");
    }
}

internal static class Program
{
    private static volatile bool _cancel;

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _cancel = true;
        };

        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (OptionException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            Console.Error.WriteLine();
            Options.PrintUsage();
            return 2;
        }

        switch (options.Mode)
        {
            case "":
                Options.PrintUsage();
                return 2;
            case "help":
                Options.PrintUsage();
                return 0;
            case "rawdump":
                return RawDump.Run(() => _cancel);
        }

        if (options.Mode is "list" or "passthrough" or "gate"
            && !Interception.TryPreload(out string loadError))
        {
            Console.Error.WriteLine("error: could not load interception.dll");
            Console.Error.WriteLine("       " + loadError);
            Console.Error.WriteLine();
            Console.Error.WriteLine("       Copy the x64 interception.dll next to mouser-phase0.exe.");
            Console.Error.WriteLine("       See README.md, section 'Deploy'.");
            return 3;
        }

        try
        {
            return options.Mode switch
            {
                "list" => ListDevices(),
                "passthrough" => RunCapture(options, gateEnabled: false),
                "gate" => RunCapture(options, gateEnabled: true),
                "recv" => Receive(options),
                "ping" => Ping(options),
                _ => Unknown(options.Mode),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("fatal: " + ex);
            return 1;
        }
    }

    private static int Unknown(string mode)
    {
        Console.Error.WriteLine($"error: unhandled mode '{mode}'");
        return 2;
    }

    private static int DriverUnavailable()
    {
        Console.Error.WriteLine("error: interception_create_context() returned NULL.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  The driver is not installed, the machine has not been rebooted");
        Console.Error.WriteLine("  since installing it, or this process cannot open \\\\.\\interception.");
        Console.Error.WriteLine("  Try again from an elevated prompt. See README.md, 'Troubleshooting'.");
        return 4;
    }

    private static int ListDevices()
    {
        IntPtr context = Interception.interception_create_context();
        if (context == IntPtr.Zero) return DriverUnavailable();

        try
        {
            Console.WriteLine("Interception devices (ids are driver slots, not stable across replug):");
            Console.WriteLine();

            bool anyMouse = false;
            for (int index = 0; index < Interception.MaxMouse; index++)
            {
                int device = Interception.MouseDevice(index);
                string id = Interception.GetHardwareId(context, device);
                if (id.Length == 0) continue;
                anyMouse = true;
                Console.WriteLine($"  mouse   {device,3}  {id}");
            }

            if (!anyMouse) Console.WriteLine("  (no mouse devices)");

            Console.WriteLine();
            for (int index = 0; index < Interception.MaxKeyboard; index++)
            {
                int device = Interception.KeyboardDevice(index);
                string id = Interception.GetHardwareId(context, device);
                if (id.Length == 0) continue;
                Console.WriteLine($"  keyboard {device,2}  {id}");
            }
        }
        finally
        {
            Interception.interception_destroy_context(context);
        }

        return 0;
    }

    private static int RunCapture(Options options, bool gateEnabled)
    {
        IntPtr context = Interception.interception_create_context();
        if (context == IntPtr.Zero) return DriverUnavailable();

        UdpSender? udp = null;

        try
        {
            List<MouseInfo> mice = Interception.EnumerateMice(context);
            if (mice.Count == 0)
            {
                Console.Error.WriteLine("error: the driver reports no mouse devices.");
                return 5;
            }

            int target = 0;
            string targetLabel = "all mice";

            if (!string.IsNullOrEmpty(options.Filter))
            {
                MouseInfo? match = mice.FirstOrDefault(m =>
                    m.HardwareId.Contains(options.Filter, StringComparison.OrdinalIgnoreCase));

                if (match is null)
                {
                    Console.Error.WriteLine($"error: no mouse hardware id contains \"{options.Filter}\".");
                    Console.Error.WriteLine("devices seen:");
                    foreach (MouseInfo candidate in mice)
                    {
                        Console.Error.WriteLine($"  {candidate.Device,3}  {candidate.HardwareId}");
                    }
                    return 6;
                }

                target = match.Device;
                targetLabel = match.HardwareId;
            }

            Console.WriteLine("mouser phase0");
            Console.WriteLine($"  device : {targetLabel}");
            Console.WriteLine($"  mode   : {(gateEnabled ? "gate" : "passthrough")}");

            if (gateEnabled)
            {
                string engage = options.HoldButton == 0
                    ? "G key only"
                    : $"hold mouse button {options.HoldButton}, or G key";
                Console.WriteLine($"  engage : {engage}");
            }

            if (options.UdpTarget is not null)
            {
                udp = new UdpSender(options.UdpTarget);
                Console.WriteLine($"  udp    : {options.UdpTarget}");
            }

            Console.WriteLine($"  keys   : {(gateEnabled ? "G toggle gate, Q quit" : "Q quit")}");
            Console.WriteLine();

            if (gateEnabled && target == 0)
            {
                Console.WriteLine("  WARNING: --filter not given, so every mouse will be gated.");
                Console.WriteLine();
            }

            Interception.SetMouseFilter(context, Interception.FilterMouseAll);
            Console.WriteLine("  filter armed. Ctrl+C, Q, or killing this process restores the mouse.");
            Console.WriteLine();

            var capture = new Capture(context, target, gateEnabled, options.HoldButton);

            var pump = new Thread(capture.Loop)
            {
                IsBackground = true,
                Name = "stroke-pump",
            };

            var reporter = new Thread(() => capture.Report(udp, () => OnStall(context, capture)))
            {
                IsBackground = true,
                Name = "reporter",
            };

            pump.Start();
            reporter.Start();

            WaitForQuit(capture, gateEnabled);

            capture.Stop();
            bool stopped = pump.Join(2000);

            Interception.SetMouseFilter(context, Interception.FilterNone);

            if (!stopped)
            {
                Console.Error.WriteLine("warning: stroke pump did not exit; terminating without cleanup.");
                Console.Out.Flush();
                Environment.Exit(0);
            }

            return 0;
        }
        finally
        {
            udp?.Dispose();
            Interception.interception_destroy_context(context);
        }
    }

    private static void OnStall(IntPtr context, Capture capture)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("!! stroke pump stalled; disabling the filter so the mouse keeps working.");
        Interception.SetMouseFilter(context, Interception.FilterNone);
        capture.Stop();
        _cancel = true;
    }

    private static void WaitForQuit(Capture capture, bool gateEnabled)
    {
        while (!_cancel)
        {
            if (Console.IsInputRedirected)
            {
                Thread.Sleep(200);
                continue;
            }

            try
            {
                while (!_cancel && Console.KeyAvailable)
                {
                    ConsoleKey key = Console.ReadKey(intercept: true).Key;

                    if (key == ConsoleKey.Q)
                    {
                        _cancel = true;
                        break;
                    }

                    if (key == ConsoleKey.G && gateEnabled)
                    {
                        capture.ToggleGate();
                        Console.WriteLine($"  gate -> {(capture.GateActive ? "ON" : "off")}");
                    }
                }
            }
            catch (InvalidOperationException)
            {
                Thread.Sleep(200);
                continue;
            }

            Thread.Sleep(20);
        }
    }

    private static int Receive(Options options)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, options.Port));
        socket.ReceiveTimeout = 250;

        Console.WriteLine($"listening on udp/{options.Port} - Ctrl+C to stop");
        Console.WriteLine("frame: magic u32 | seq u32 | qpc i64 | dx f32 | dy f32 | wheel f32 | gate u16 | pad u16");
        Console.WriteLine();

        var buffer = new byte[2048];
        long packets = 0;
        long bytes = 0;
        long gaps = 0;
        long lastPackets = 0;
        long lastBytes = 0;
        long lastTimestamp = Stopwatch.GetTimestamp();
        uint lastSequence = 0;
        bool haveSequence = false;

        while (!_cancel)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int read;

            try
            {
                read = socket.ReceiveFrom(buffer, ref from);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.ConnectionReset)
            {
                read = 0;
            }

            if (read > 0)
            {
                if (read == Wire.PingSize && BinaryPrimitives.ReadUInt32LittleEndian(buffer) == Wire.MagicPing)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer, Wire.MagicPong);
                    try
                    {
                        socket.SendTo(buffer, 0, read, SocketFlags.None, from);
                    }
                    catch (SocketException)
                    {
                        // A lost pong is not interesting.
                    }

                    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  ping from {from}");
                    continue;
                }

                if (read >= Wire.DataSize && BinaryPrimitives.ReadUInt32LittleEndian(buffer) == Wire.MagicData)
                {
                    uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4));

                    if (haveSequence)
                    {
                        int delta = unchecked((int)(sequence - lastSequence));
                        if (delta > 1) gaps += delta - 1;
                    }

                    lastSequence = sequence;
                    haveSequence = true;
                    packets++;
                    bytes += read;
                }
            }

            long now = Stopwatch.GetTimestamp();
            double elapsed = (now - lastTimestamp) / (double)Stopwatch.Frequency;
            if (elapsed < 1.0) continue;

            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0:HH:mm:ss}  {1,5:F0} pkt/s  {2,7:F1} KB/s  gaps {3}",
                DateTime.Now,
                (packets - lastPackets) / elapsed,
                (bytes - lastBytes) / elapsed / 1024.0,
                gaps));

            lastTimestamp = now;
            lastPackets = packets;
            lastBytes = bytes;
        }

        return 0;
    }

    private static int Ping(Options options)
    {
        if (options.UdpTarget is null)
        {
            Console.Error.WriteLine("error: --ping needs --udp host:port");
            return 2;
        }

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveTimeout = 1000;

        Console.WriteLine($"pinging {options.UdpTarget} (start --recv on the other machine first)");
        Console.WriteLine();

        var buffer = new byte[64];
        var samples = new List<double>(32);

        for (int i = 0; i < 20 && !_cancel; i++)
        {
            var packet = new byte[Wire.PingSize];
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0), Wire.MagicPing);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)i);
            BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(8), Stopwatch.GetTimestamp());

            long start = Stopwatch.GetTimestamp();

            try
            {
                socket.SendTo(packet, options.UdpTarget);
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int read = socket.ReceiveFrom(buffer, ref from);

                if (read < Wire.PingSize || BinaryPrimitives.ReadUInt32LittleEndian(buffer) != Wire.MagicPong)
                {
                    Console.WriteLine($"  seq {i,2}  unexpected reply");
                }
                else
                {
                    double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                    samples.Add(ms);
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  seq {0,2}  rtt {1,6:F2} ms", i, ms));
                }
            }
            catch (SocketException)
            {
                Console.WriteLine($"  seq {i,2}  timeout");
            }

            Thread.Sleep(100);
        }

        if (samples.Count == 0)
        {
            Console.Error.WriteLine("no replies - is --recv running, and is the firewall open?");
            return 7;
        }

        samples.Sort();
        Console.WriteLine();
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "rtt  min {0:F2}  median {1:F2}  p95 {2:F2}  max {3:F2} ms  ({4} replies)",
            samples[0],
            samples[samples.Count / 2],
            samples[Math.Min(samples.Count - 1, (int)(samples.Count * 0.95))],
            samples[^1],
            samples.Count));

        return 0;
    }
}
