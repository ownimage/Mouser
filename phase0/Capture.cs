using System.Diagnostics;

namespace Mouser.Phase0;

/// <summary>
/// The stroke pump. Runs on its own thread because
/// interception_wait_with_timeout blocks, and nothing in this class is allowed
/// to allocate or block while strokes are in flight.
/// </summary>
internal sealed class Capture
{
    private readonly IntPtr _context;
    private readonly int _targetDevice;
    private readonly bool _gateEnabled;

    private readonly ushort _holdDown;
    private readonly ushort _holdUp;

    private readonly Interception.MouseStroke[] _incoming = new Interception.MouseStroke[64];
    private readonly Interception.MouseStroke[] _outgoing = new Interception.MouseStroke[64];
    private readonly long[] _latencySamples = new long[8192];
    private int _latencyIndex;

    private volatile bool _stop;
    private volatile bool _holdGate;
    private volatile bool _toggleGate;
    private long _lastAlive;

    private long _frameDx;
    private long _frameDy;
    private long _frameWheel;
    private long _strokeCount;
    private long _gatedCount;
    private long _sendErrors;

    public Capture(IntPtr context, int targetDevice, bool gateEnabled, int holdButton)
    {
        _context = context;
        _targetDevice = targetDevice;
        _gateEnabled = gateEnabled;

        (_holdDown, _holdUp) = holdButton switch
        {
            3 => (Interception.MouseMiddleDown, Interception.MouseMiddleUp),
            4 => (Interception.MouseButton4Down, Interception.MouseButton4Up),
            5 => (Interception.MouseButton5Down, Interception.MouseButton5Up),
            _ => ((ushort)0, (ushort)0),
        };

        _lastAlive = Stopwatch.GetTimestamp();
    }

    public bool GateActive => _gateEnabled && (_toggleGate || _holdGate);

    public void ToggleGate() => _toggleGate = !_toggleGate;

    public void Stop() => _stop = true;

    /// <summary>True when the pump has not ticked for long enough to worry.</summary>
    public bool IsStalled => ElapsedMs(ref _lastAlive) > 500.0;

    private bool IsTarget(int device) => _targetDevice == 0 || device == _targetDevice;

    public void Loop()
    {
        while (!_stop)
        {
            int device = Interception.interception_wait_with_timeout(_context, 50);
            Interlocked.Exchange(ref _lastAlive, Stopwatch.GetTimestamp());

            if (device <= 0) continue;
            if (Interception.interception_is_mouse(device) == 0) continue;

            int count = Interception.interception_receive(_context, device, _incoming, (uint)_incoming.Length);
            if (count <= 0) continue;

            long start = Stopwatch.GetTimestamp();
            bool target = IsTarget(device);
            int keep = 0;

            for (int i = 0; i < count; i++)
            {
                ref Interception.MouseStroke mouse = ref _incoming[i];

                // Re-read the gate per stroke: a batch can contain the mode
                // button press followed by motion in the same receive.
                bool gating = target && GateActive;

                // A mouse stroke carries no type tag. Motion is the stroke with no
                // button or wheel bits set; 0x1000 is a filter bit, never a stroke
                // flag. ATTRIBUTES_CHANGED is excluded so a display change still
                // reaches the OS while gated.
                bool motion = mouse.state == 0
                    && (mouse.flags & Interception.FlagAttributesChanged) == 0;

                if (gating && motion)
                {
                    Interlocked.Add(ref _frameDx, mouse.x);
                    Interlocked.Add(ref _frameDy, mouse.y);
                    Interlocked.Increment(ref _gatedCount);
                    continue;
                }

                if (gating && (mouse.state & Interception.WheelMask) != 0)
                {
                    Interlocked.Add(ref _frameWheel, mouse.rolling);
                    Interlocked.Increment(ref _gatedCount);
                    continue;
                }

                if (target && _holdDown != 0)
                {
                    if ((mouse.state & _holdDown) != 0) _holdGate = true;
                    else if ((mouse.state & _holdUp) != 0) _holdGate = false;
                }

                _outgoing[keep++] = mouse;
            }

            if (keep > 0 && Interception.interception_send(_context, device, _outgoing, (uint)keep) == 0)
            {
                Interlocked.Increment(ref _sendErrors);
            }

            Interlocked.Add(ref _strokeCount, count);

            long elapsed = Stopwatch.GetTimestamp() - start;
            long nanoseconds = (long)(elapsed * (1_000_000_000.0 / Stopwatch.Frequency));
            Interlocked.Exchange(ref _latencySamples[_latencyIndex], nanoseconds);
            _latencyIndex = (_latencyIndex + 1) & 8191;
        }
    }

    /// <summary>
    /// Reporting, UDP framing, and the watchdog. Deliberately on a different
    /// thread from the pump so a slow socket or a slow console can never delay
    /// stroke forwarding.
    /// </summary>
    public void Report(UdpSender? udp, Action onStall)
    {
        long lastReport = Stopwatch.GetTimestamp();
        long lastStrokes = 0;
        long lastGated = 0;
        bool stallReported = false;

        while (!_stop)
        {
            Thread.Sleep(10);

            long dx = Interlocked.Exchange(ref _frameDx, 0);
            long dy = Interlocked.Exchange(ref _frameDy, 0);
            long dz = Interlocked.Exchange(ref _frameWheel, 0);
            bool gate = GateActive;
            udp?.Send(dx, dy, dz, gate);

            if (IsStalled)
            {
                if (!stallReported)
                {
                    stallReported = true;
                    onStall();
                }
                continue;
            }

            long now = Stopwatch.GetTimestamp();
            double elapsed = (now - lastReport) / (double)Stopwatch.Frequency;
            if (elapsed < 1.0) continue;

            long strokes = Interlocked.Read(ref _strokeCount);
            long gated = Interlocked.Read(ref _gatedCount);
            (double p50, double p99) = LatencyPercentiles();

            Console.WriteLine(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:HH:mm:ss}  gate={1}  strokes {2,6:F0}/s  gated {3,6:F0}/s  loop p50 {4:F3} ms  p99 {5:F3} ms  err {6}",
                DateTime.Now,
                gate ? "ON " : "off",
                (strokes - lastStrokes) / elapsed,
                (gated - lastGated) / elapsed,
                p50,
                p99,
                Interlocked.Read(ref _sendErrors)));

            lastReport = now;
            lastStrokes = strokes;
            lastGated = gated;
        }
    }

    private (double P50, double P99) LatencyPercentiles()
    {
        var samples = new List<long>(4096);
        for (int i = 0; i < _latencySamples.Length; i++)
        {
            long value = Interlocked.Read(ref _latencySamples[i]);
            if (value > 0) samples.Add(value);
        }

        if (samples.Count == 0) return (0, 0);

        samples.Sort();
        int p50 = (int)(samples.Count * 0.50);
        int p99 = Math.Min(samples.Count - 1, (int)(samples.Count * 0.99));
        return (samples[p50] / 1_000_000.0, samples[p99] / 1_000_000.0);
    }

    private static double ElapsedMs(ref long timestamp)
    {
        long value = Interlocked.Read(ref timestamp);
        return (Stopwatch.GetTimestamp() - value) * 1000.0 / Stopwatch.Frequency;
    }
}
