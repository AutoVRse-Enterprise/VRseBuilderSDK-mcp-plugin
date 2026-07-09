<p align="center">
  <img src="icon.png" alt="VRseBuilder Unity MCP" width="180" />
</p>

# VRseBuilder Unity MCP Bridge Plugin

A Unity Editor plugin that lets AI assistants (Claude, etc.) control the Unity Editor via the [Model Context Protocol (MCP)](https://modelcontextprotocol.io). It is the Unity half of the **VRseBuilder Unity MCP** toolchain by AutoVRse.

## What It Does

This package runs a lightweight HTTP server inside the Unity Editor on `localhost:7890`. The companion **VRseBuilder Unity MCP Server** (Node.js) connects to it, exposing **200+ tools** to AI assistants across **30+ feature categories**.

**Core Capabilities:**

- **Scene Management** — Open, save, create scenes; browse full hierarchy tree
- **GameObjects** — Create (primitives or empty), delete, inspect, set transforms (world/local)
- **Components** — Add/remove components, get/set any serialized property
- **Assets** — List, import, delete assets; create prefabs and materials; assign materials
- **Scripts** — Create, read, update C# scripts
- **Builds** — Trigger multi-platform builds (Windows, macOS, Linux, Android, iOS, WebGL)
- **Console & Compilation** — Read errors/warnings/logs, clear console; get C# compilation errors via CompilationPipeline (independent of console buffer)
- **Play Mode** — Play, pause, stop
- **Editor** — Execute menu items, run arbitrary C# code, check editor state, get project info

**Extended Capabilities:**

- **Animation** — List clips, get clip info, list Animator controllers and parameters, set Animator properties, play animations
- **Prefab (Advanced)** — Open/close prefab editing mode, check prefab status, get overrides, apply/revert changes
- **Physics** — Raycasts, sphere/box casts, overlap tests, get/set physics settings (gravity, layers, collision matrix)
- **Lighting** — Manage lights, configure environment lighting/skybox, bake lightmaps, list/manage reflection probes
- **Audio** — Manage AudioSources, AudioListeners, AudioMixers, play/stop clips, adjust mixer parameters
- **Navigation** — NavMesh baking, agents, obstacles, off-mesh links
- **Particles** — Particle system creation, inspection, module editing
- **UI** — Canvas, UI elements, layout groups, event system
- **Tags & Layers** — List tags and layers, add/remove tags, assign tags/layers to GameObjects
- **Selection** — Get/set editor selection, find objects by name/tag/component/layer
- **Graphics** — Scene and game view capture as inline images for visual inspection
- **Input Actions** — List action maps and actions, inspect bindings (Input System package)
- **Assembly Definitions** — List, inspect, create, update .asmdef files
- **ScriptableObjects** — Create, inspect, modify ScriptableObject assets
- **Constraints** — Position, rotation, scale, aim, parent constraints
- **LOD** — LOD group management and configuration

**Profiling & Debugging:**

- **Profiler** — Start/stop profiler, get stats, take deep profiles, save profiler data
- **Frame Debugger** — Enable/disable frame debugger, get draw call list and details, get render target info
- **Memory Profiler** — Memory breakdown by asset type, top memory consumers, take memory snapshots (with `com.unity.memoryprofiler` package)

**Infrastructure:**

- **Multi-Instance Support** — Multiple Unity Editor instances discovered automatically (including ParrelSync clones)
- **Port Affinity** — Each editor remembers its last-used port via EditorPrefs and reclaims it on restart
- **Registry Heartbeat** — The plugin sends a heartbeat every 30 seconds to the shared instance registry, so the MCP server can distinguish compiling editors from crashed ones
- **Multi-Agent Support** — Multiple AI agents can connect simultaneously with session tracking, action logging, and queued execution
- **Play Mode Resilience** — MCP bridge survives domain reloads during Play Mode via SessionState persistence
- **Project Context** — Auto-inject project-specific documentation and guidelines for AI agents (via `Assets/MCP/Context/`)

## Installation via Unity Package Manager

1. Open Unity > **Window** > **Package Manager**
2. Click the **+** button > **Add package from git URL...**
3. Enter:
   ```
   https://github.com/AutoVRse-Enterprise/VRseBuilderSDK-mcp-plugin.git
   ```
4. Click **Add**

Unity will download and install the package. Once loaded, the bridge starts a local HTTP server on port `7890` (see the Console for a startup message).

### Verify

Open a browser and visit: `http://127.0.0.1:7890/api/ping`

You should see JSON with your Unity version and project name.

## Companion: MCP Server

This plugin is one half of the system. You also need the **VRseBuilder Unity MCP Server** (Node.js) that connects Claude to this bridge.

## Dashboard

Open **Window > VRseBuilder Unity MCP** to access:

- Server status with live indicator (green = running, red = stopped)
- Start / Stop / Restart controls
- Per-category feature toggles (enable/disable any of the 30+ categories)
- Port and auto-start settings
- Active agent session monitoring

## Requirements

- Unity 2021.3 LTS or newer (tested on 2022.3 LTS and Unity 6)
- .NET Standard 2.1 or .NET Framework

### Optional Packages

Some features activate automatically when their corresponding packages are detected:

| Package / Asset | Features Unlocked |
|----------------|-------------------|
| `com.unity.memoryprofiler` | Memory snapshots via MemoryProfiler API |
| `com.unity.shadergraph` | Shader Graph create, inspect, open |
| `com.unity.visualeffectgraph` | VFX Graph listing and opening |
| `com.unity.inputsystem` | Input Action maps and bindings inspection |
| `com.unity.multiplayer.playmode` | MPPM scenario management (list, activate, start/stop, status) |
| Amplify Shader Editor (Asset Store) | Amplify shader listing, inspection, opening |

## Configuration

Configuration is managed through the dashboard (`Window > VRseBuilder Unity MCP > Settings`):

- **Port** — HTTP server port (default: `7890`)
- **Auto-Start** — Automatically start the bridge when Unity opens (default: `true`)
- **Category Toggles** — Enable/disable any feature category

Settings are stored in `EditorPrefs` and persist across sessions.

## Security

- The server **only** binds to `127.0.0.1` (localhost) — it is not accessible from the network
- No authentication is required since it's local-only
- All operations support Unity's Undo system
- Multi-agent requests are queued to prevent conflicts

## License

See [LICENSE](LICENSE).
