# TDR2000 Tools

> [!WARNING]
> **DEVELOPMENT / INITIAL DRAFT (`dev` build)**  
> This is a personal pet project in early development.  
> It contains experimental code choices, unpolished UI elements, work-in-progress features, and known bugs.  
> Mechanics and outputs are unstable and subject to frequent changes.

Desktop toolkit and C# library for inspecting, extracting, and converting **Carmageddon: TDR 2000 PAK files**.

---

## Technical Overview

`TDR2000 Tools` provides a cross-platform Avalonia UI application and core library (`TDR.PakLib`, `TDR.Formats`) to interface directly with TDR2000 archive structures and asset files.

### Key Capabilities
- **Virtual File System (VFS):** Reads and writes TDR2000 Trie-indexed `.DIR` directories and `.PAK` containers with XOR keying and `zIG` zlib compression.
- **Texture & Material Decoding:** Renders 32-bit RGBA (`_32`), 24-bit RGB (`_24`), and 8-bit paletted TGA images. Supports `.png` conversion with alpha channel and `map_Bump` relief maps for water and terrain shaders.
- **Audio Inspection & Playback:** In-memory WAV/SND header decoding (Sample Rate, Bits/Channels, Duration) with real-time playback timer, discrete progress bar, mute controls, and seamless **Looping (🔁)** for motor/ambient sounds.
- **Track & Geometry Parsing:** Resolves 3D hierarchy models (`.hie`), binary mesh containers (`.mshs`), movables placements (`MoveableDescriptor.txt`), powerup icons (`.pup`), and pedestrian placements (`PEDS_DESCRIPTOR`).
- **3D Level Export:** Generates combined Wavefront `.OBJ` (with `.mtl` material libraries), `.gltf` 2.0 scenes, and structured `scene.json` manifests.
- **Variant & Layer Support:** Supports Base Track Only, specific Race/Mission variants, or All Variants combined export modes with auto-unpacking for inner `.PAK` archives.

---

## Workspace Layout & User Interface

The application features a dual-panel desktop workspace:

1. **Left Panel (VFS & File System Explorer):**
   - Tree View (`TreeView`), Flat Details Table (`ListBox`), or Grid Cards View (`WrapPanel`).
   - Toolbar navigation buttons (Back `‹`, Forward `›`, Up `↑`) with dynamic `IsEnabled` history states and custom vector icons.
   - Quick search and filtering across indexed virtual files.

2. **Right Panel (Preview & Inspector):**
   - **Image Preview:** View TGA textures and paletted graphics.
   - **Audio Inspector Drawer:** Interactive WAV audio player with real-time ticking time counter (`0:00 / MM:SS`), progress bar, Mute toggle, and Infinite Loop mode for engine/ambient sounds. Double-click any audio file in VFS tree to play instantly.
   - **Metadata Inspector:** View raw file properties, compression details, mesh node counts, and archive locations.
   - **Log Console:** Resizable session log console with draggable vertical splitter, multi-line text selection (`Ctrl+C` copy), right-click context menu, and a dedicated **Clear Log** button.

3. **Track Conversion Modal:**
   - Triggered by double-clicking a Track Badge or choosing **Export Track to OBJ / glTF...** in the context menu.
   - **Left Panel (Format & Geometry Controls):** Scrollable configuration panel containing format flags (`.OBJ`, `.GLTF 2.0`, `scene.json`), texture PNG conversion, `Also unpack inner .PAK archives before export` option, coordinate modes (`Local Coordinates`, `Raycast GroundSnap`), and grouping options. Settings automatically persist across sessions in `settings.json`.
   - **Right Panel (Presets & Resource Tree):** Top Preset selector (`All supported resources`, `Base Track Only`, `All Races`, `All Missions`, or `Custom Selection`) synchronized with a 2-tier checkable `TreeView` of physical track layer roots (`Hollowood`, `Hollowood_Race1`, `Hollowood_Mission1`) and VFS subfolders (`Level Convsoft`, `Level Breakable`, `Sky Sphere`). Cascading check states allow toggling entire race layers or individual `.hie` meshes in a single click. Non-renderable camera path and cutscene script files (`campaths`, `intpaths`, `zoomin`, `lookat`) are un-checked by default.

---

## Project Structure

```text
├── TDR2000Tools.slnx         # Solution manifest
├── lib/
│   ├── TDR.PakLib/           # Core VFS archive reader/writer & zIG compression library
│   └── TDR.Formats/          # HIE, MSHS, TGA, PUP, and Track descriptor parsers
├── src/
│   └── TDR.Tools/            # Desktop GUI Application (Avalonia UI, .NET 10)
├── docs/
│   ├── EXPORT_FORMAT.md      # Detailed format & keyword specification contract
│   └── TDR2000_Tools_Architecture.md # Technical architecture & decompilation notes
├── tests/                    # Unit & format verification test project
├── clean.bat                 # Helper script to clean build artifacts (bin/obj)
└── rebuild_release.bat       # Helper script to clean and perform a Release build
```

---

## Building from Source

### Prerequisites
- [.NET 10.0 SDK](https://dotnet.microsoft.com/)

### Build Commands

```bash
# Clone repository
git clone https://github.com/username/TDR2000-Tools.git
cd TDR2000-Tools

# Build Solution
dotnet build TDR2000Tools.slnx -c Release

# Run Desktop Application
dotnet run --project src/TDR.Tools/TDR.Tools.csproj
```

On Windows, you can also run `rebuild_release.bat` to clean previous build outputs and compile a Release build.

---

## Supported Formats Reference

| Format | Extension | Description | Status |
| :--- | :--- | :--- | :--- |
| **Archive Directory** | `.dir` | Trie-indexed directory structure | Fully Supported (Read/Write) |
| **Archive Container** | `.pak` | Data container with XOR/zIG payloads | Fully Supported (Read/Write) |
| **Model Hierarchy** | `.hie` | ASCII 3D hierarchy and node transform tree | Fully Supported |
| **Binary Mesh** | `.mshs` / `.msh` | Binary vertex, normal, UV, and face data | Fully Supported |
| **Image Asset** | `.tga` / `.tx` / `.pal` | Uncompressed TGA, TTEX descriptor, palette | Fully Supported |
| **Level Descriptor** | `.txt` | Level asset keywords and placement descriptors | Fully Supported |
| **Powerup Descriptor** | `.pup` | 3D item placement coordinates and Type IDs | Fully Supported |
| **Volume Collision** | `.h` / `.scol` | Environment & checkpoint bounding volumes | Read / Export Supported |
| **Audio Sample** | `.wav` / `.snd` | PCM audio samples, engine revs, and ambient sounds | In-Memory Playback & Seamless Loop (🔁) |

---

## Current Status & Known Scope Limits

- **Format Scope:** Designed specifically for Carmageddon: TDR 2000 formats.
- **Pedestrian Animations:** Pedestrian positions and waypoints are exported to metadata (`scene.json`); skeletal animation playback is handled in external viewers/engines.
- **Raycast Snapping:** Movable objects use approximate coordinates from level descriptors; an experimental Raycast snapper (`GroundSnapUtil`) is available for terrain surface alignment.
