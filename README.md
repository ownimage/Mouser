# Mouser

Turning an ordinary mouse into a 6DOF input device for Windows 11.

**Phase 0 (this code)** answers one question: *can we read the mouse below raw input
and stop it reaching the OS?* Nothing here does 6DOF yet. It is the go/no-go test
for the whole project.

---

## Which machine runs this

**The machine the mouse is physically plugged into.** Not this host.

The capture path is an Interception mouse-class filter driver. It only sees HID
strokes that exist on the machine where the device stack lives. RDP does not
forward your mouse: `mstsc` reads the local pointer and re-transmits it as
absolute screen coordinates over the RDP virtual-mouse channel. By the time it
arrives here, the physical device is gone.

This was verified on this host over RDP:

```
abs=( 7684,27321)      <- absolute RDP coordinates, not stroke deltas
note: device names unavailable on this system (seen on some RDP sessions)
```

`MOUSE_MOVE_ABSOLUTE` plus a dead `GetRawInputDeviceInfo` is the signature of a
remote session, not a real mouse.

| Component | Machine |
|---|---|
| Interception driver + `mouser-phase0.exe` | the laptop or desktop with the mouse |
| 6DOF consumer (later phases) | this host, or wherever the app runs |
| Build toolchain | anywhere with the .NET 8 SDK |

Consequence: **this host needs no driver at all.** While the gate is engaged the
RDP mouse channel is dead by design, so the 6DOF stream has to travel over a side
channel (UDP, Phase 0c) rather than through RDP.

---

## What Phase 0 proves

| Check | Mode | Pass condition |
|---|---|---|
| Driver loads on Win11 as configured | `--list` | prints your mouse's hardware id |
| Strokes survive a round trip intact | `--passthrough` | mouse feels completely normal |
| Legacy path is blocked | `--gate` | cursor freezes, no app receives motion |
| Raw input path is blocked | `--gate` + `--rawdump` | raw reader goes silent |
| Network is fast enough | `--ping` | median RTT well under 10 ms on LAN |

The `--rawdump` check is the important one. A `WH_MOUSE_LL` hook would fail it;
that is the reason for the driver.

---

## Prerequisites

**Build machine:** .NET 8 SDK. On this host nothing was installed, so:

```powershell
winget install Microsoft.DotNet.SDK.8
# or, without admin:
irm https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\dotnet-install.ps1
& $env:TEMP\dotnet-install.ps1 -Channel 8.0 -InstallDir "$env:USERPROFILE\.dotnet" -NoPath
```

`build.ps1` finds a user-local SDK automatically if `dotnet` on PATH has no SDK.

**Target machine:** nothing. The publish is self-contained; one folder, no runtime
install, no admin needed to *run* (the driver install needs admin).

**Interception release:** <https://github.com/oblitum/Interception/releases>
Extract it; the layout is

```
Interception/
  library/x64/interception.dll      <- ships with the app
  driver/interception.sys           <- installed once, machine-wide
  driver/interception.inf
  command line installer/install-interception.exe
```

---

## Build

```powershell
cd P:\git\Mouser

# self-contained, bundles interception.dll, produces out\ and mouser-phase0.zip
.\build.ps1 -InterceptionDir C:\path\to\Interception\library\x64
```

Without `-InterceptionDir` the build still succeeds but warns that
`interception.dll` is missing from `out\`.

Useful switches:

| Switch | Effect |
|---|---|
| `-FrameworkDependent` | ~200 KB folder instead of ~70 MB, needs .NET 8 runtime on target |
| `-NoZip` | skip packaging |
| `-Output <path>` | publish elsewhere |

If you have no SDK at all and only want to compile-check on this host:

```powershell
dotnet build phase0\Mouser.Phase0.csproj -c Release
```

---

## Deploy

The target is a different machine, so get the folder across:

**Option A — RDP drive redirection.** In `mstsc`: *Local Resources → More → Drives*.
From the client machine:

```powershell
Copy-Item '\\tsclient\P\git\Mouser\mouser-phase0.zip' $env:USERPROFILE\Desktop\
Expand-Archive $env:USERPROFILE\Desktop\mouser-phase0.zip -DestinationPath $env:USERPROFILE\Desktop\mouser
```

**Option B — network share.** Copy `out\` anywhere and run it. There is no installer
and no registry state; it is a plain folder.

Run it from a console so you can see the telemetry:

```powershell
cd $env:USERPROFILE\Desktop\mouser
.\mouser-phase0.exe --list
```

---

## Install the driver (target machine, elevated, once)

**Before you install**, check whether this machine will accept it:

```powershell
Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard |
    Select-Object VirtualizationBasedSecurityStatus, SecurityServicesRunning
```

`SecurityServicesRunning` containing `2` means Memory Integrity / HVCI is on. That
is the single most likely reason the driver will refuse to load.

```powershell
# elevated prompt, in the folder containing install-interception.exe
Unblock-File .\install-interception.exe
.\install-interception.exe /install
# reboot
```

If it installs but the app reports a null context, see *Troubleshooting*.

---

## Testing (Phase 0 acceptance)

This is the definitive procedure. Run it **on the machine with the mouse**, from an
elevated console, with a spare mouse or a PS/2 keyboard within arm's reach.

Budget roughly 20 minutes plus one reboot. Work top to bottom and stop at the
first failure — do not skip ahead to T4 hoping it works, because a failure in T2
means the forwarding loop is already wrong and T4 will mislead you.

| Test | What it proves | Time |
|---|---|---|
| T1 | driver loads and enumerates your mouse | 2 min |
| T2 | forwarding is lossless | 3 min |
| T3 | the gate consumes motion | 2 min |
| T4 | **raw input is beaten** — the whole point of the project | 2 min |
| T5 | momentary engage works | 1 min |
| T6 | other pointing devices are untouched | 2 min |
| T7 | crash and resume leave the mouse usable | 5 min |
| T8 | the network hop is fast enough | 3 min |

### Pre-flight

```powershell
# Will this machine accept an old cross-signed driver?
Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard |
    Select-Object VirtualizationBasedSecurityStatus, SecurityServicesRunning

# Secure Boot state, relevant to the same question
try { Confirm-SecureBootUEFI } catch { "Secure Boot state unavailable" }

# Test signing should be OFF for a clean result
bcdedit /enum '{current}' | Select-String 'testsigning|hypervisorlaunchtype'
```

Write the output down. If `SecurityServicesRunning` contains `2`, Memory Integrity /
HVCI is on and T1 is at real risk — read the signature entry under *Troubleshooting*
before you start, so a failure is not a surprise.

Set two variables once, to keep the rest of the procedure readable:

```powershell
$m = '.\mouser-phase0.exe'
$f = 'VID_046D'      # replace with the substring you identify in T1
```

### T1. Driver is alive

```powershell
& $m --list
```

**Pass:** your mouse appears with a plausible `VID_`/`PID_` pair, and you know the
substring to use for `$f`. **Fail:** null context — see *Troubleshooting*.

Expect something like:

```
Interception devices (ids are driver slots, not stable across replug):

  mouse    11  HID\VID_046D&PID_C52B&MI_02&Col01\...
  mouse    12  HID\VID_...          <- touchpad, second mouse, etc.

  keyboard  1  HID\VID_...
```

Note the substring you want for `--filter` — usually `VID_xxxx&PID_xxxx`, or the
whole string.

### T2. Forwarding is lossless

```powershell
& $m --passthrough --filter $f
```

Five sub-tests, all of which must pass. A lossy pass-through is worse than no tool
at all, so do not rationalise a stutter here.

- **T2a motion** — sweep the cursor across the full desktop, slowly and then fast.
  Must be indistinguishable from the driver not being installed.
- **T2b clicks** — left, right, middle, and double-click, in Explorer and a browser.
- **T2c drag** — drag a window by its title bar for several seconds. The drag must not
  drop or hesitate mid-move.
- **T2d wheel** — scroll a long document in both directions. No reversed ticks, no
  overshoot, no dead notches.
- **T2e side buttons** — MB4/MB5 in a browser. Back and forward must still work.

Expect roughly:

```
10:16:45  gate=off  strokes    982/s  gated      0/s  loop p50 0.031 ms  p99 0.118 ms  err 0
```

`loop p50` is our own processing time per batch, not end-to-end latency. Sub-millisecond
is healthy; a p99 above a few ms means something is blocking the pump.

### T3. The gate consumes motion

```powershell
& $m --gate --filter $f
```

Press `G`. The cursor must freeze while the counts keep climbing:

```
10:16:52  gate=ON   strokes    984/s  gated    980/s  loop p50 0.028 ms  p99 0.101 ms  err 0
```

`gated` tracking `strokes` is the proof that motion is being consumed rather than
merely ignored. Then, still gated:

- **Clicks still land.** Open the Start menu and click something. Buttons are never
  dropped, by design, so you can always recover.
- **Scroll does nothing.** Scroll wheel must produce no motion in any window.
- Press `G` again and the cursor resumes instantly.

### T4. Raw input is dead (the acceptance criterion)

Two consoles, side by side:

```powershell
# console 1
& $m --gate --filter $f

# console 2
& $m --rawdump
```

1. Move the mouse. Console 2 prints `rel=(...)` lines.
2. Focus console 1, press `G`. It reports `gate=ON` and `gated` climbs with `strokes`.
3. Move the mouse again. **Console 2 must print absolutely nothing.**
4. Press `G`. Console 2 resumes on the next stroke.

**Pass:** complete silence in console 2 while gated. No `WH_MOUSE_LL` hook can
produce that result, which is why this project uses a driver.

Three things to check while you are here, because they separate a working result
from a lucky one:

- **Negative control.** Run console 1 as `--passthrough` instead. Console 2 must
  keep printing while you move. If it stays silent in passthrough too, the test is
  broken, not passed.
- **Selective, not total.** While gated, click a button. Console 2 must show `btn=`
  lines while still showing no `rel=` lines.
- **`rel=` not `abs=`.** On a real mouse it must say `rel`. If it says `abs`, you are
  inside an RDP or VM session and this test is meaningless — go to the physical
  machine.

### T5. Momentary engage

```powershell
& $m --gate --filter $f --hold 4
```

Do not press `G`. Hold MB4 and move the mouse: the cursor must freeze, and the
telemetry must read `gate=ON ` only while the button is held. Release, and motion
returns immediately.

**Expected quirk:** the MB4 press also delivers a real MB4 click to whatever has
focus, so a browser may navigate back. That is deliberate in Phase 0 — buttons are
never swallowed. Phase 1 swallows the down/up pair together. Try `--hold 3`
(middle button) if MB4 is too disruptive for testing.

### T6. Other pointing devices are untouched

With `--gate --filter $f` running and gated:

- If the machine has a touchpad, use it. The pointer must move normally.
- If you have a second mouse, plug it in and use it. It must work normally while the
  filtered mouse is frozen.

This is the capability a global `WH_MOUSE_LL` hook cannot provide, and the main
practical reason the driver is worth its installation cost.

Also confirm the safety warning appears when you omit `--filter`:

```powershell
& $m --gate          # should print: WARNING: --filter not given, so every mouse will be gated
```

Quit immediately; do not leave an unfiltered gate running.

### T7. Crash and resume

The mouse must survive the tool dying badly.

**Kill while gated.** In console 2:

```powershell
Start-Sleep -Seconds 8
taskkill /F /IM mouser-phase0.exe
```

In console 1, engage the gate with `G` and wait. When the process is killed, the
mouse must start working again within about a second, with no reboot. The filter is
reverted when the last handle to `\\.\interception` closes.

**Sleep and resume.** With the tool running and the gate off, sleep the machine,
wake it, and move the mouse. Motion and telemetry must both resume. Then engage the
gate once more and confirm it still freezes.

If the mouse is ever left dead, `Ctrl+Alt+Del` still works — use the keyboard to
reboot. Uninstall the driver before trying again.

### T8. Network hop (Phase 0c)

On the receiving machine:

```powershell
& $m --recv --port 7801
```

On the machine with the mouse:

```powershell
& $m --ping --udp 192.168.1.50:7801
& $m --gate --filter $f --udp 192.168.1.50:7801
```

`--ping` reports round-trip time. The telemetry stream is 32 bytes at ~100 Hz
(`magic u32 | seq u32 | qpc i64 | dx f32 | dy f32 | wheel f32 | gate u16 | pad u16`);
`--recv` reports packet rate, gaps, and echoes the pings.

**Pass:** median RTT under 1 ms on wired LAN, under 5 ms on Wi-Fi, and zero `gaps`
while moving the mouse. Above 20 ms the device will feel wrong no matter how good
the input path is.

If the firewall blocks it:

```powershell
New-NetFirewallRule -DisplayName 'mouser phase0' -Direction Inbound -Protocol UDP -LocalPort 7801 -Action Allow
```

### Record the result

Fill this in and keep it with the repo; it is the Phase 1 starting condition.

| Test | Result | Notes |
|---|---|---|
| T1 driver loads | | HVCI state: |
| T2 forwarding lossless | | |
| T3 gate consumes motion | | |
| **T4 raw input silent** | | **decisive** |
| T5 momentary engage | | |
| T6 other devices unaffected | | |
| T7 crash and resume | | |
| T8 network RTT | | median ms, host: |

### Go / no-go

- **T4 passes** — the architecture is sound and Phase 1 can start.
- **T4 fails but T1–T3 pass** — the driver is loaded but not filtering. Confirm
  `--filter` matched the device you actually moved. Then check that motion detection in
  `Capture.cs` still reads `mouse.state == 0`: `0x1000` is
  `INTERCEPTION_FILTER_MOUSE_MOVE`, it belongs to the filter word, and it never appears
  in a stroke, so testing `flags & 0x1000` silently gates nothing.
- **T1 fails** — signature or HVCI. Pick one of the three options under
  *Troubleshooting* before writing any more code.
- **T2 fails** — a bug in the forwarding loop. Fix it before doing anything else; a
  lossy pass-through would make every later measurement a lie.

---

## Uninstall / rollback

```powershell
# elevated
.\install-interception.exe /uninstall
# reboot
```

To stop the app: `Q`, `Ctrl+C`, or kill the process. The filter is reverted when
the last handle to `\\.\interception` closes, so a crashed process should not
leave the mouse disabled. **Verify this for yourself once**, with a spare mouse or a
PS/2 keyboard within reach, before trusting it.

---

## Troubleshooting

**`could not load interception.dll`**
`interception.dll` is not next to the exe, or you copied the x86 one. It must be
the x64 build from `library\x64`.

**`interception_create_context() returned NULL`**
In order of likelihood: not rebooted since install; driver blocked by HVCI;
running unelevated; wrong session. Check:

```powershell
pnputil /enum-drivers | Select-String -Context 3,1 interception
Get-WinEvent -LogName System -MaxEvents 50 |
    Where-Object { $_.Message -match 'interception' } |
    Select-Object TimeCreated, Id, Message
```

**Driver refuses to install, or "cannot verify the digital signature"**
Interception is old and its signature may not satisfy a Win11 24H2 machine with
Memory Integrity and Secure Boot on. Options, in order of preference:

1. Custom KMDF mouse filter signed via Microsoft's attestation portal (weeks, needs a certificate).
2. Test-signing mode — `bcdedit /set testsigning on`, disable Memory Integrity, reboot. Works, adds a desktop watermark, and many anti-cheat drivers will refuse to run.
3. Abandon the driver and use a `WH_MOUSE_LL` hook — fine everywhere except raw-input consumers, which is the one case you said matters.

**`--gate` does nothing at all**
Motion in an Interception mouse stroke has `state == 0`. There is no type tag and no
move bit in a stroke, so anything testing `flags & 0x1000` will never gate. That value
is `INTERCEPTION_FILTER_MOUSE_MOVE` and belongs only in the argument to
`interception_set_filter`.

**`AccessViolationException` in `interception_set_filter`**
The arguments are in the wrong order. The real signature is
`interception_set_filter(context, predicate, filter)`; passing the filter second makes
the DLL call `0xFFFF` as a function pointer. Call `Interception.SetMouseFilter`
instead of the import directly.

**Gating freezes the wrong mouse**
Add `--filter`. Without it, every mouse is gated and the tool warns you.

**Every `--gate` press also fires a side-button click**
Expected in Phase 0. Button strokes are always forwarded, so pressing MB4 to engage
also delivers an MB4 click to the focused app. Phase 1 swallows the down/up pair
together and the phantom click disappears.

**`--rawdump` shows `dev:0x...` instead of a name**
Cosmetic. `GetRawInputDeviceInfo` is unavailable in some RDP sessions. The handle
still distinguishes devices.

---

## Safety rails already in the code

- **Mice only.** The filter is installed with `interception_is_mouse`. A stuck
  keyboard filter would make the machine unusable, so that path does not exist.
- **Only motion and wheel are ever dropped.** Buttons always pass, so you can
  always still click things.
- **Watchdog.** If the stroke pump stops ticking for 500 ms, the reporter thread
  disables the filter and exits rather than leaving you with a dead mouse.
- **Allocation-free hot path.** No locks, no logging, no UI on the pump thread.
- **The reporter is a separate thread**, so a slow socket or a slow console can
  never delay forwarding.

---

## What Phase 0 deliberately does not do

- No 6DOF mapping, curves, deadbands or axis locking.
- No mode buttons doing anything useful — `--hold` only toggles the gate.
- No persistent mode state, no config file, no tray UI.
- No virtual device. Nothing yet pretends to be a SpaceMouse.
- Latency is measured as our processing time plus network RTT, not as perceived
  input-to-photon latency through an application.

---

## Layout

```
Mouser/
  build.ps1                 publish + bundle interception.dll + zip
  phase0/
    Mouser.Phase0.csproj
    Interception.cs         P/Invoke for the filter driver
    Capture.cs              stroke pump, gate logic, telemetry, watchdog
    RawInput.cs             hidden Win32 window + WM_INPUT witness
    Net.cs                  UDP framing
    Program.cs              CLI, modes, driver lifecycle
  out/                      published, self-contained, ready to copy
```

## Next: Phase 1

Once `--rawdump` goes silent under `--gate`, the hard part is done and the work
turns into product design: arm/disarm, paired button swallowing, the axis mapper,
the clutch integrator, and a transport that the target application actually
consumes.
