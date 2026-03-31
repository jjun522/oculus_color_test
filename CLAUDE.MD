# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

# Eye Color VR Project - 시신경 병증 테스트 (Optic Neuropathy Test)

## Project Purpose
VR-based optic neuropathy inspection system. A web operator panel controls what a patient sees in an Oculus Quest 3S headset — adjusting brightness, color, and screen division mode per eye — and mirrors the VR display in real time.

## Stack
- **Backend**: Python + FastAPI (`server.py`) — WebSocket relay + UDP discovery
- **Frontend (Web)**: React 19 + Vite (`front/`) — operator control panel
- **Frontend (VR)**: Unity C# (`unity/VRController.cs`) — Oculus Quest 3S display engine

## Run Commands

### Backend
```bash
conda activate eye_color
python server.py 12346        # Default port 12346
```

### Frontend (Web)
```bash
cd front
npm install                   # First time only
npm run dev
npm run build                 # Build to front/dist/
```

### Testing utilities
```bash
python test_ws.py             # Test WebSocket connection
python test_udp_listen.py     # Test UDP discovery broadcast
```

### Docker (full stack)
```bash
docker compose up
```

## Architecture Overview

### Communication Flow
```
VR Device (Unity) ←──WebSocket──→ server.py ←──WebSocket──→ Web Panel (React)
                                      ↑
                               UDP broadcast :50002
                          (VR auto-discovers server IP)
```

**server.py** is a pure 1:1 relay. First client to connect is assigned role WEB, second is VR. The server strips the `TARGET:DEVICE_ID:` prefix from web messages before forwarding the bare command to VR.

### WebSocket Message Protocol

| Direction | Format | Example |
|-----------|--------|---------|
| VR → Server | `REG:device_unique_id` | Registration |
| VR → Web | JSON `VRStateData` | Full state sync (every 0.1s) |
| Web → VR | `TARGET:ALL:COMMAND` | Relay strips to `COMMAND` |
| Server → Web | `DEVICE_LIST:device_id` | VR connect/disconnect status |

**Commands (Web → VR):**
- `SET_VAL:TARGET:VALUE` — brightness 0–100 (e.g. `SET_VAL:L_NASAL:75`)
- `CFG:KEY:VALUE` — config (e.g. `CFG:DISTANCE:5.5`, `CFG:SCALE:1.0`)
- `MODE_2` / `MODE_4V` / `MODE_4` — division mode
- `EYE_LEFT` / `EYE_RIGHT` / `EYE_BOTH` — eye target
- `CHANGE_COLOR` / `CHANGE_TARGET` — cycle color or quadrant target
- `BRIGHT_UP` / `BRIGHT_DOWN` — ±2% brightness
- `TOGGLE_FLIP` / `SET_FLIP_INTERVAL:N` — alternating-eye flip mode

### Shared State (`VRStateData`)
Both Unity and React maintain the same state shape:

```
divMode        : 2=2-split, 3=vertical-4, 4=cross-4
colorIdx       : color index into cfgColorOrder
currentEyeTarget: 0=left, 1=right, 2=both
isFlipMode / isLeftEyeShown / leftFlipAdj
leftNasal, leftTemporal, rightNasal, rightTemporal  (2-split brightness %)
q0–q3          (cross-4 quadrant brightness %)
leftTop, leftBot, rightTop, rightBot               (vertical-4 brightness %)
cfgScale, cfgDistance, cfgFlipInterval
cfgDefaultBright, cfgTargetBright, cfgBgBright
cfgColorOrder  (comma-separated color index order, e.g. "0,1,2,3")
```

Unity serializes this to JSON and sends it; React deserializes and mirrors the display.

### Unity VR Rendering (`unity/VRController.cs`)
- Uses OVRCameraRig; **CenterEyeAnchor is disabled** so left/right cameras render independently.
- **Layer 30** = left eye only, **Layer 31** = right eye only — prevents cross-eye bleed.
- 8 Quad groups are pre-created at `Start()`: 4 modes × 2 eyes (`L_Whole`, `R_Whole`, `L_Split`, `R_Split`, `L_Vert`, `R_Vert`, `L_Cross`, `R_Cross`). Only the relevant group is activated per mode.
- All quads use `Unlit/Color` shader (no lighting influence).
- **Flip mode** (alternating eye) only activates when `divMode=2` AND `currentEyeTarget=2`. It zeroes the opposing eye's cullingMask each frame.
- Brightness formula in React (lux display): `lux = 87 × (percent/100)^2.2 × π`

### React Web Panel (`front/src/App.jsx`)
Single-file component (~540 lines). Key sections:
- **WebSocket hook**: connects to `ws://localhost:12346/ws`, receives JSON state updates, updates `vrState`.
- **`renderMirrorScreen()`**: renders a pixel-accurate preview of what the VR headset is showing.
- **`renderControlTab()`**: mode buttons, brightness inputs, flip controls. Independent brightness inputs change dynamically based on `divMode`.
- **`renderSettingsTab()`**: quad scale, distance, brightness levels, color order.
- All commands sent as `sendCommand("TARGET:ALL:COMMAND")`.
- Vite proxies `/ws` → `http://localhost:12346` (see `vite.config.js`).

## Critical Constraints
- **Left/right eye independence must not be broken**: any layout change in Unity must keep Layer 30/31 assignments correct and keep `CenterEyeAnchor` disabled.
- **1:1 relay only**: `server.py` does not support multiple simultaneous VR devices (role assignment is first-come first-served).
- **Flip mode guard**: flip logic in Unity is conditional on `divMode=2 && currentEyeTarget=2` — do not relax this without understanding the rendering implications.
