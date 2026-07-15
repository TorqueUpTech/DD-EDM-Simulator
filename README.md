# EDM Simulator — Dyno Command PC Test Console (Windows)

A C# **WPF** desktop app that acts as the **handheld** so the dyno protocol can
be verified on a PC — with or without hardware. It implements the same wire
format as the original, reusing the original's exact CRC, framing and packet math.

Its reason to exist is the **EDM Simulator**: the default build can act *as* the
dyno's EDM, hosting a Bluetooth SPP server that replays a captured datalog to a
connecting handheld. That is how the Android handheld gets exercised with no dyno
attached. See [Build variants](#build-variants) and
[EDM Simulator](#edm-simulator-test-rig-sim-build-only).

## Related repos
| Repo | What |
|------|------|
| [DynoCommandPC](https://github.com/TorqueUpTech/DynoCommandPC) | The handheld WPF app this was forked from — the one that gets day-to-day feature work. |
| [Dyno-Command-EDM](https://github.com/TorqueUpTech/Dyno-Command-EDM) | The Android handheld port, plus `Documentation/PROTOCOL.md` (the wire format both implement) and the project skills. |

> **Heads-up:** the handheld code here (`Protocol/`, `ViewModel/`, `Transport/`)
> is a July-2026 snapshot of DynoCommandPC and has since diverged from it — the
> canonical app has newer transport and ramp work. Treat this repo as the
> simulator; port fixes from DynoCommandPC rather than assuming parity.

## How it connects
The app connects straight to the EDM's Bluetooth address over classic RFCOMM/SPP
(`BluetoothRfcommTransport`, raw Winsock `AF_BTH`, SDP-resolved channel). **No
pairing/bonding and no COM port needed** — works with a connectable,
non-discoverable, non-bonding EDM. Set the MAC in Settings and **Connect** (or
tick **Auto-connect to the EDM at startup**). This mirrors what the original
handheld did, and is confirmed working against real EDM hardware.

## Build & run
```powershell
# Stop a running instance first — the build locks DynoCommandPC.exe
Get-Process DynoCommandPC -ErrorAction SilentlyContinue | Stop-Process -Force

cd DynoCommandPC
dotnet run            # or: dotnet build -c Release
```
Requires the .NET 8 SDK. The default (Sim) build targets
`net8.0-windows10.0.19041.0` — it needs the Win10 SDK TFM for the WinRT Bluetooth
APIs the simulator's SPP server uses.

### Build variants
Three variants come from this one codebase, selected by MSBuild properties:

| Command | Variant | TFM |
|---------|---------|-----|
| `dotnet build` | **Sim** (default) — full app **incl. the EDM Simulator**; title shows "— Sim build", `EDM_SIM` defined | `net8.0-windows10.0.19041.0` |
| `dotnet build -p:EdmSim=false` | Handheld only — `Server/*` and `EdmServerWindow` compiled out, sim button hidden | `net8.0-windows` |
| `dotnet build -p:DynoNet=true` | Handheld over TCP/IP via the DynoServer relay (implies non-sim); `DYNO_NET` defined | `net8.0-windows` |

Outputs land in per-TFM folders, so the variants don't clobber each other.

## Settings (⚙ Settings…)
Configurable and persisted to `settings.json` **beside the executable** (created
with defaults on first run — the PC equivalent of the original `PdaSettings.xml`):
- **Connection** — EDM Bluetooth address, and **Auto-connect at startup**.
- **Dyno type** — 4WD bed (enables the balance packet).
- **Speed** — units (kph/mph/rpm), road/engine control, roller diameter, gear
  ratio, per-unit max speed, and the small/large nudge increments (which drive the
  target buttons). Units/diameter/gear feed the TX encoding.
- **Diagnostics** — show/hide the packet-log and "packet types seen" panels.

## What you can do
- Pick a **mode** (No Load, Speed, Load, Ramp Up/Hold/Down, Brake, Hands-free,
  Set Bed) — drives the dyno-mode / HFR / digital-inputs packets.
- Set **target speed / load / ramp rate / bed position** (±buttons or type).
  In **Speed** mode the simulator chases the target; the big read-out shows the
  decoded km/h coming back — proving the loop end-to-end.
- Set **Fan 1/2** modes (PC/Off/On/Auto) and the **4WD** flag (adds the balance
  packet). Trigger **Snapshot** / **Clear Graph**.
- **Diagnostics:** live TX/RX/error counts, last TX & RX packet in hex, link age,
  and an optional rolling packet log (tick "Log packets").

## EDM Simulator test rig (Sim build only)
The app can play the **dyno side**: it hosts a Bluetooth SPP server and replays a
captured datalog to a connecting handheld, so the whole handheld path — framing,
CRC, parser, handshake responders, governor, UI — can be verified with no dyno and
no EDM.

1. Run the **Sim build** (default); the title bar reads "— Sim build". Click **EDM Sim…**.
2. **Browse** to a capture. [`dyno-capture-20260627-103431.txt`](dyno-capture-20260627-103431.txt)
   in this repo is a known-good sample. It must be a `dyno-capture-*.txt` (it carries
   the RX-direction frames incl. speed / shootout / config) — **not** a `dash-log`
   CSV, which is a different format and yields 0 frames.
3. **Start server.** Status goes to "listening — waiting for handheld"; note the
   **Bluetooth address** it shows.
4. On the handheld (the Android app, or a second PC), set the EDM address to that
   MAC and **Connect**.

It uses a native WinRT `RfcommServiceProvider` + `StreamSocketListener` with
`PlainSocket` (no pairing, works unpackaged), re-wrapping each frame through
`PacketFramer` with the CRC recomputed, preserving the original inter-frame timing.

## Layout
```
DynoCommandPC.sln
dyno-capture-20260627-103431.txt   # sample log the simulator replays
DynoCommandPC/
  Server/            # the EDM Simulator (Sim build only)
    EdmSimServer.cs     — WinRT RFCOMM SPP server + replay loop
    EdmReplaySource.cs  — parses a dyno-capture log, extracts RX frames + timing
  EdmServerWindow.xaml(.cs)  # its UI (Browse / Start / Stop, shows PC BT MAC)
  Protocol/        # reusable, hardware-agnostic — port these to Kotlin verbatim
    CanPacket, Crc16, Enums, PacketFramer (wrap + push-parser),
    PacketFactory (TX builders), PacketParser (RX decode), CommandState, DynoState
  Transport/
    ITransport, BluetoothRfcommTransport (AF_BTH SPP — direct to the EDM),
    TcpDynoTransport (DynoServer relay over TCP — DynoNet build)
  DynoController.cs  # TX loop (~20 Hz) + RX framer/parser, mirrors COM_Comms
  ViewModel/, MainWindow.xaml, App.xaml
```

## Verified
A headless integration test confirmed the wire format: all packet types
round-trip byte-identical through wrap→stream→deframe→CRC; a 0xA0-in-body packet
survives byte-stuffing; and a corrupted CRC is rejected. The protocol is also
**confirmed working against real EDM hardware** over Bluetooth. The `Protocol/`
folder is the layer ported to Android unchanged.

## Notes / next steps
- Speed is shown in **km/h + roller revs/s**; the full kph/mph/rpm unit system and
  engine-vs-road scaling is a parity feature to add later.
- Against a real EDM: set the MAC in Settings, Connect, and the read-outs populate
  from live hardware. Confirm the speed scaling (`×0.002 / GearRatio`, roller
  diameter) matches the dyno's own display.
</content>
