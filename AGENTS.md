# AGENTS.md

Context for a fresh session. Read this before changing anything. It records the
decisions that were expensive to reach and the invariants that keep the tool safe
to run on a machine with only one mouse.

Repo: `P:\git\Mouser`, branch `main`. Current state: **Phase 0 complete but
unverified on real hardware** (see *Verified vs unverified*).

---

## The goal

Turn an ordinary mouse into a 6DOF input device on Windows 11. The mouse exposes
three channels — X, Y, wheel — plus two side buttons. Two modes:

- **MB4 held** → rotation: yaw, pitch, roll
- **MB5 held** → translation: X, Y, Z

Three channels times two modes is six degrees of freedom, but only three at once.
That is fine for CAD and DCC navigation and useless for a flight sim. Do not
promise simultaneous 6DOF.

The user has an additional hard requirement: **it must also defeat applications
that read raw input directly**, i.e. games. Everything below follows from that.

---

## Decision 1: a driver, not a hook

`WH_MOUSE_LL` cannot do this job. Raw input is produced from the device stack
*below* the hook chain, so swallowing legacy messages hides motion from the cursor
and from normal apps but not from anything using `WM_INPUT` or DirectInput.

The alternatives were checked and rejected:

| Approach | Why it fails |
|---|---|
| `WH_MOUSE_LL`, return 1 | does not affect raw input; 300 ms callback budget; silently removed on timeout |
| `RIDEV_NOLEGACY` | suppresses legacy synthesis only; other apps' raw input still flows |
| `ClipCursor` / cursor warping | breaks on multi-monitor and DPI, fights other apps, ignores raw input |
| Exclusive HID `CreateFile` | Windows refuses for mouse collections |
| Replace the device driver with WinUSB | then you have no mouse at all |

The chosen mechanism is **Interception** (https://github.com/oblitum/Interception),
a `mouclass` filter driver. Nothing reaches the OS unless the app calls
`interception_send()`, so blocking is the default and pass-through is the special
case.

Accepted costs, already agreed with the user: machine-wide install, admin, reboot,
an old and unmaintained driver, and the possibility that HVCI or anti-cheat
rejects it.

---

## Decision 2: capture runs on the machine with the mouse

RDP does not forward the physical mouse. `mstsc` reads the local pointer and
re-transmits absolute screen coordinates over its own virtual-mouse channel.

Verified empirically on the dev host over RDP:

```
abs=( 7684,27321)     <- MOUSE_MOVE_ABSOLUTE, not stroke deltas
note: device names unavailable on this system (seen on some RDP sessions)
```

`GetRawInputDeviceInfo` also fails there for both `RIDI_DEVICENAME` and
`RIDI_DEVICEINFO`, even with `cbSize` correctly initialised. That is a property of
the session, not a bug in our code.

Therefore:

| Component | Machine |
|---|---|
| Interception + `mouser-phase0.exe` | the laptop or desktop the mouse is plugged into |
| 6DOF consumer | this host, or wherever the app runs |
| Build | anywhere with the .NET 8 SDK |

**The host needs no driver.** While gated, the RDP mouse channel is dead by
design, so the 6DOF stream must travel over a side channel (UDP), not through RDP.

Useful corollary: `mstsc` in relative-mouse mode is itself a raw-input consumer,
which is an independent reason a hook would not have sufficed.

---

## Decision 3: hold-to-engage

Momentary, not toggle. Holding MB4 or MB5 gates motion; releasing restores normal
mouse behaviour. Chosen by the user, and it is the safer default: a crash cannot
strand the machine in a gated state.

Phase 1 will add an armed/disarmed supervisor on top, so MB4/MB5 keep their normal
function when the tool is not armed.

---

## Decision 4: Phase 0 emits telemetry only

No transport has been chosen. `--udp` is a measurement probe, not an architecture
commitment. Keep any eventual `OutputSink` behind an interface and do not let a
transport decision leak into the capture path.

---

## Decision 5: C# .NET 8, self-contained, no UI framework

- Console app, `net8.0`, x64 only, published self-contained so the target needs no
  runtime installed (~70 MB, 32 MB zipped).
- **No WinForms.** It was used for one hidden window in the first draft and pulled
  in all of WPF, taking the payload from 70 MB to 144 MB. `RawInput.cs` now creates
  the window with hand-rolled Win32 P/Invoke.
- A user-local SDK was installed on the dev host at `C:\Users\Keith\.dotnet`
  (no admin, no machine-wide change). `build.ps1` finds it automatically.

---

## Code map

| File | Responsibility |
|---|---|
| `phase0/Interception.cs` | P/Invoke surface, stroke/mouse structs, hardware-id enumeration |
| `phase0/Capture.cs` | stroke pump, gate state, telemetry, UDP framing hand-off, watchdog |
| `phase0/RawInput.cs` | hidden Win32 window, `RIDEV_INPUTSINK`, the raw-input witness |
| `phase0/Net.cs` | wire constants and the UDP sender |
| `phase0/Program.cs` | CLI parsing, modes, driver lifecycle, receiver and ping |
| `build.ps1` | publish, bundle `interception.dll`, zip |
| `README.md` | build, deploy, the T1–T8 acceptance procedure, troubleshooting |

---

## Invariants — do not break these

1. **The pump thread allocates nothing.** `Capture.Loop` may not allocate, lock, log,
   touch the console, or do socket I/O. If you need to add work, put it on the
   reporter thread and hand over a snapshot.
2. **The reporter is a separate thread** so a slow socket or a slow console can never
   delay stroke forwarding.
3. **The watchdog stays.** If the pump stops ticking for 500 ms, the reporter
   disables the filter and exits. This is the difference between a bug and a dead
   mouse.
4. **Filter mice only.** `INTERCEPTION_FILTER_MOUSE_ALL` with `interception_is_mouse`.
   Never add a keyboard filter; a stuck one makes the machine unusable.
5. **Buttons always pass in Phase 0.** This is why `--hold 4` also fires a real MB4
   click. It is deliberate and documented. Phase 1 must swallow the down/up pair
   together, or phantom clicks will persist.
6. **Never call `interception_destroy_context` while the pump might be blocked**
   inside `interception_wait_with_timeout`. The code joins the pump first and calls
   `Environment.Exit` if the join times out.
7. **`Interception.MouseStroke` is a 20-byte `InterceptionMouseStroke`, not a tagged
   union.** `InterceptionStroke` is `char[20]` and the device slot tells you the type.
   Field order is `state, flags, rolling, x, y, information`, with two bytes of padding
   before `x`. Use explicit layout; `Interception.cs` asserts the size at load time.
   Note `state` holds buttons and wheel bits — motion is `state == 0`.
8. **`RAWINPUTHEADER` is 24 bytes on x64**, `RAWMOUSE` 24, `RAWINPUT` 48. Do not
   "tidy" the explicit field offsets.
9. **`interception_set_filter(context, predicate, filter)` — predicate is second.**
   The filter is a `unsigned short`; passing it where the predicate belongs makes the
   DLL call `0xFFFF` as a function pointer and the process dies with an
   `AccessViolationException`. Always go through `Interception.SetMouseFilter`.

---

## Verified vs unverified

Verified on the dev host (no driver, RDP session only):

- clean Release build, zero warnings; self-contained publish
- `--rawdump` parses real `WM_INPUT` data correctly (struct layout confirmed)
- UDP loopback: 20 pings, 0.25 ms median RTT, echo and gap counting correct
- missing-`interception.dll` path returns exit code 3 with a useful message
- published binary in `out\` runs

**Verified on real hardware** (one run, after the ABI fix):

- `--list` opens a driver context and prints the correct hardware id for the mouse
  (`HID\VID_046D&PID_C52B\...`), so the driver loads, the context is valid, and the
  `IOCTL_GET_HARDWARE_ID` path works.

**Unverified:** everything past that — the stroke pump, forwarding, and the gate. The
first hardware run crashed in `interception_set_filter` because of the two ABI mistakes
now recorded as invariants 7 and 9. T2–T8 in `README.md` must run on the machine with
the mouse.

---

## Risks, in order of severity

1. **HVCI / driver signing.** The dominant risk. If the machine will not load it,
   the fallback ladder is in `README.md` under *Troubleshooting*: attestation-signed
   custom KMDF filter, or test-signing mode, or abandon games and use a hook.
2. **`interception_wait_with_timeout` is assumed to be exported.** It is used instead
   of `interception_wait` so `Ctrl+C` is graceful. If it is missing the app will
   throw `EntryPointNotFoundException` immediately — which is at least obvious.
3. **Anti-cheat.** Kernel anti-cheat may refuse to run alongside an unknown filter
   driver. Fine for single-player, risky for competitive.
4. **Unmaintained upstream.** Windows Updates may break it; a custom signed filter
   driver is the eventual answer.

---

## Environment gotchas already paid for

- **Verify the binary before trusting a result.** The first hardware run of T2 used a
  stale `mouser-phase0.exe` and produced a stack trace that looked like a live bug.
  `--version` now prints `0.2.0+<sha>[-dirty]`; check it on the test machine first.
- Windows PowerShell 5.1 is the shell here. `$PSScriptRoot` is **empty during
  parameter-default binding**; assign defaults in the script body. Avoid `?.` and
  `??`.
- `CreateParams.Style = 0x80000000` needs `unchecked((int)0x80000000)`.
- `GetRawInputDeviceInfo` for `RID_DEVICEINFO` requires the caller to set
  `cbSize`; for `RIDI_DEVICENAME` it can fail entirely in RDP sessions. Always zero
  the buffer before reading it — an earlier version printed uninitialised heap as a
  device name.
- Setting `Console.OutputEncoding` can throw when stdout is redirected; wrap it.

---

## Commands

```powershell
# build + bundle + zip
.\build.ps1 -InterceptionDir C:\path\to\Interception\library\x64

# compile check only
& "$env:USERPROFILE\.dotnet\dotnet.exe" build phase0\Mouser.Phase0.csproj -c Release

# runtime modes
.\mouser-phase0.exe --list
.\mouser-phase0.exe --passthrough --filter VID_046D
.\mouser-phase0.exe --gate --filter VID_046D [--hold 4] [--udp 192.168.1.50:7801]
.\mouser-phase0.exe --rawdump
.\mouser-phase0.exe --recv --port 7801
.\mouser-phase0.exe --ping --udp 192.168.1.50:7801
```

`out/`, `bin/`, `obj/` and `*.zip` are gitignored.

---

## Roadmap

**Phase 1 — product shell.** Armed/disarmed supervisor (so MB4/MB5 keep their normal
function when idle), paired button swallowing, `AxisMapper` (deadband, per-axis gain,
expo, invert, axis lock, fine-mode multiplier), clutch integrator (hold to move,
release to hold position, optional recenter), JSON config, tray UI.

**Phase 2 — output.** `OutputSink` interface, then shared memory or a named pipe, plus
a throwaway 3D preview so the mapping feel can be judged locally rather than over
RDP. Judge feel at the machine with the mouse; remote display latency dominates
everything else.

**Phase 3 — real device presence.** Virtual HID (VHF) exposing a genuine 6-axis
device so Blender, Fusion and Unreal pick it up natively, or a custom filter that
both strips motion and adds a HID collection. This is where the certificate work
from the Phase 0 decision pays off.

---

## Conventions

- File-scoped namespaces, nullable enabled, `sealed` classes, no external packages.
- Comments explain *why*, not *what*. The existing comments mark places where a
  naive change is dangerous.
- Keep `README.md` honest: mark what has been tested on real hardware and what has
  not. The T4 test is the only one that decides whether the architecture works.
- Do not commit `out/` or the zip.
