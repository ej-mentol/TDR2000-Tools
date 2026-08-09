# TDR2000 Tools & Exporter — Cumulative Architecture & Specification Document

> **Status:** Active Reference & Design Specification  
> **Target Project:** TDR2000 Tools Ecosystem (Sandbox: `tdr2obj`)  
> **Schema Version:** 1.0  

---

## 1. Executive Overview

This document captures empirical reverse-engineering discoveries, format specifications, pipeline architecture, and future design patterns for Carmageddon: TDR 2000 asset processing and level conversion.

---

## 2. Binary & Text Format Specifications

### 2.1 Virtual File System (`PakManager` & TDR Archive Trie)
- **Trie Index (`.dir` / `.pak`):** TDR2000 archives use a Trie index directory (`.dir`) referencing uncompressed or zIG compressed streams inside `.pak` containers.
- **Precedence Rule:** Loose files on disk override packed archive entries (`Loose files win over archive entries`).
- **Path Resolution:** Relative virtual paths (e.g. `tracks/hollowood/hollowoodmesh.hie`) must be preserved to maintain correct track context rather than flattening everything into a single directory.

### 2.2 Hierarchical Model Parser (`.hie`)
- **Version Variants:**
  - `Version 2` (ASCII): Standard hierarchy structure (Matrix list, Texture list, Material list, Mesh list, Node list).
  - `Version 3` (ASCII): Extended hierarchy format containing `CollisionDataMeshes` (`// Number of Collision Data meshes`), extra line names, and dynamic collision attributes (`.dcol`).
  - `Version 257` (`0x0101` Binary / HIE loader `sub_47A020`): Contains an explicit animation frame rate `float` parameter (defaults to `60.0 FPS` if absent).
- **Comment Parsing Trap:** Section headers (e.g. `// Node list :`) are followed by human-readable column descriptor lines (e.g. `// TYPE INDEX CHILD SIBLING`).
  - *Rule:* Parsers MUST skip decorative column comment lines (`// ...`) while retaining main section boundaries (`IsKnownSectionHeader`).
- **Nodes = 0 Fallback:** Certain static level models (`HollowoodMesh.hie`) and simple movables omit the node tree (`Nodes.Count == 0`, `Root == null`).
  - *Rule:* Fall back to direct mesh rendering (`hie.Meshes`) when `Nodes.Count == 0`.
- **Node Types:**
  - `1`: Matrix (Transform)
  - `2`: Texture (Material/Texture switch)
  - `3`: Mesh (MSHS index reference)
  - `5`: Material (TDRMaterial assignment)

### 2.3 Binary Mesh Container (`.mshs` / `.msh`)
- **`MeshMode` Enum Values:**
  - `NGon = 0` (`0x0000`)
  - `TriIndexedPosition = 256` (`0x0100`)
  - `Tri = 512` (`0x0200`)
- **Vertex Normal Generation:** `MeshMode.Tri` models often lack pre-computed vertex normals in binary. Normals must be accumulated from face triangle winding and normalized per vertex.

### 2.4 Master Track Descriptor Keyword Registry (`sub_49C280` Decompilation)

Decompilation of `sub_49C280` reveals the exact C++ keyword registry used by `TDR2000.exe` when parsing `{TrackName}.txt` level files:

| Keyword Token | Memory Offset | Type / Format | Description / Status |
| :--- | :--- | :--- | :--- |
| `NAME` | `this + 8` | String | Track display name |
| `DESCRIPTION` | `this + 16` | String | Track description |
| `TRACK_SELECT_MESH` | `this + 24` | String | UI Track select 3D mesh |
| `TRACK_SELECT_RENDER_NODE` | `this + 32` | Int | UI Track select render node ID |
| `TRACK_SELECT_TEX` | `this + 36` | String | UI Track select preview texture |
| `SPLASH_SCREEN_MESH` | `this + 44` | String | Loading splash screen 3D mesh |
| `SPLASH_SCREEN_RENDER_NODE` | `this + 52` | Int | Loading splash screen render node ID |
| `SKY_SPHERE` | `this + 56` | String | Skybox `.hie` model |
| `SKY_SPHERE_RENDER_NODE` | `this + 64` | Int | Skybox render node ID |
| `SKY_SPHERE_OPTIMISE` | `this + 68` | Int | Skybox optimization toggle |
| `WATER_MESH` | `this + 84` | String | Water surface `.hie` model |
| `WATER_RENDER_NODE` | `this + 92` | Int | Water surface render node ID |
| `WATER_LEVEL` | `this + 96` | Float (1) | Height of water plane |
| `TEXTURE_ANIM_DESCRIPTOR` | `this + 108` | String | Animated texture sequence descriptor |
| `START_POS` | `this + 116` | Float (3) | Player spawn position `[X, Y, Z]` |
| `START_ANGLE` | `this + 128` | Float (1) | Player spawn heading angle |
| `PALETTE` | `this + 232` | String | Color palette override |
| `RACELINE` | `this + 240` | String | Racing line waypoint file *(Disassembly-Only / Optional)* |
| `RACELINEBM` | `this + 248` | String | Racing line bitmap |
| `SEGMENTS` | `this + 256` | String | Track collision segments |
| `RADAR_DESCRIPTOR` | `this + 264` | String | HUD Radar / Minimap descriptor |
| `DRONE_DESCRIPTOR` | `this + 272` | String | Traffic drones descriptor |
| `PEDS_DESCRIPTOR` | `this + 280` | String | Pedestrian spawners descriptor |
| `ALIENS_DESCRIPTOR` | `this + 288` | String | Alien spawners descriptor |
| `ZOMBIES_DESCRIPTOR` | `this + 296` | String | Zombie spawners descriptor |
| `ARTICULATED_BRIDGES` | `this + 304` | String | Movable / articulated bridge descriptor |
| `SPECIAL_VOLUMES` | `this + 312` | String | Special enviro volume descriptor (`SPECIALV_ENVIRONMENTS`) |
| `SPECIAL_VOLUMES_0` | `this + 320` | String | Special H-enviro volume descriptor (`SPECIALV_H_ENVIRONMENTS`) |
| `ANIMATED_PROPS` | `this + 328` | String | Animated props descriptor |
| `LIGHTS_DESCRIPTOR` | `this + 336` | Light fixtures descriptor |
| `LEVEL_SCRIPT` | `this + 344` | String | Track mission logic script (`.script`) |
| `BREAKABLES_DESCRIPTOR` | `this + 352` | String | Breakable props descriptor |
| `STATIC_MESH_DESCRIPTOR` | `this + 360` | String | Main level static geometry `.hie` |
| `OCCLUDER_MESH` | `this + 368` | String | Occlusion culling mesh |
| `AMBIENT_LIGHT` | `this + 376` | Float (1) | Ambient light intensity |
| `DYN_LIGHT_TRACK` | `this + 380` | Int | Dynamic track lighting toggle |
| `SUN_VECTOR` | `this + 384` | Float (3) | Direct sun direction `[X, Y, Z]` |
| `HARDSHADOW_HIE` | `this + 396` | String | Shadow geometry `.hie` model |
| `PHYSICS_DESCRIPTOR` | `this + 404` | String | Custom track physics descriptor |
| `WEATHER_DESCRIPTOR` | `this + 412` | String | Weather effects descriptor (rain/snow) |
| `SPECIAL_HENVIRO` | `this + 420` | String | Special H-enviro settings |
| `SPECIAL_ENVIRO` | `this + 428` | String | Special enviro settings |
| `SPECIAL_SFX_ENVIRO` | `this + 436` | String | Special SFX enviro settings |
| `PATH_FOLLOWERS` | `this + 444` | String | Moving object spline paths |
| `AMBIENT_SOUNDS` | `this + 452` | String | Ambient audio track |
| `AMBIENT_LIGHT_COLOUR` | `this + 460` | Float (3) | Ambient light color `[R, G, B]` |
| `SUN_LIGHT_COLOUR` | `this + 472` | Float (3) | Direct sun color `[R, G, B]` |
| `MOVABLE_OBJECTS` | `this + 484` | String | Movables placement descriptor |
| `BASE_PATH` | `this + 492` | String | Track base directory path |
| `STEAM_NODES` | `this + 500` | String | Particle / steam emitter nodes |
| `FOG_TRACK` | `this + 508` | Int (0/1) | Track fog enable toggle |
| `FOG_TRACK_COLOUR` | `this + 512` | Float (3) | Track fog color `[R, G, B]` |
| `FOG_TRACK_DIST` | `this + 524` | Float (1) | Track fog distance |
| `FOG_TRACK_OFFSET` | `this + 528` | Float (1) | Track fog offset |
| `FOG_TRACK_NEAR` | `v136 (Stack)` | Float (1) | *Dead Field: Read into local stack, discarded by engine* |
| `FOG_TRACK_FAR` | `v136 (Stack)` | Float (1) | *Dead Field: Read into local stack, discarded by engine* |
| `FOG_SKY` | `this + 532` | Int (0/1) | Sky fog enable toggle |
| `FOG_SKY_COLOUR` | `this + 536` | Float (3) | Sky fog color `[R, G, B]` |
| `FOG_SKY_NEAR` | `this + 548` | Float (1) | Sky fog near clip distance |
| `FOG_SKY_FAR` | `this + 552` | Float (1) | Sky fog far clip distance |
| `RESPAWN_POINTS` | `this + 556` | String | AI/Player respawn points *(Disassembly-Only / Optional)* |
| `FLAG_WAVER_ON` | `this + 564` | Int | Race flag waver NPC toggle |
| `FLAG_WAVER_POS` | `this + 568` | Float (3) | Race flag waver NPC position `[X, Y, Z]` *(Disassembly-Only)* |
| `FLAG_WAVER_ANGLE` | `this + 580` | Float (1) | Race flag waver NPC heading angle *(Disassembly-Only)* |

---

## 3. Level Geometry & Layering Observations

### 3.1 Multi-Layer Track Architecture
TDR2000 levels consist of multiple overlay layers sharing a base terrain model:
1. **Base Terrain Mesh:** `{TrackName}Mesh.hie` (e.g., `HollowoodMesh.hie`).
2. **Environmental Water & Sky:** `{TrackName}Water.hie`, `FilmSkysphereStudio.hie`.
3. **Race Variant Layers:** `FilmStudioRace1.hie`, `FilmStudioRace2.hie`, `hollowoodRace1Consoft.hie`.
   - *Note:* Importing all race layers simultaneously overlays conflicting directional arrows, barrier cones, and spawn markers.
4. **Mission Variant Layers:** `Hollowood_Mission1_cryptlook.hie`, `Hollowood_Mission1_zoomin.hie`.
5. **UI / HUD Radar Planes:** `HollowoodMapMesh.hie`, `HollowoodMap_MultiMesh.hie`.
   - *Note:* These are 2D minimap/radar projections for the in-game HUD and should not be treated as 3D world geometry.

### 3.2 Movables (`MovableDescriptor.txt`)
- Descriptor format: `"HieName.hie"  X  Y  Z  QX  QY  QZ  QW`
- Spawns movable objects in world space.
- Grouping: In OBJ export, grouping instances by `g <HieName>` allows 3D editors (Blender / 3ds Max) to select and transform whole object classes (e.g. all `Lightpole` instances) in a single operation.

### 3.3 Special Engine Markers (`UserData` & Articulated Splines)
- **`UserData` Particle Emitter Quad Planes:**
  `HollowoodUserData.hie` contains 2D billboard quad planes assigned materials like `allflames`. In `TDR2000.exe`, these quad planes serve as 3D spatial markers for fire, smoke, and steam particle emitters. In static OBJ exports, they appear as 2D textured planes intersecting terrain geometry. In game engines (Unity/Unreal/Godot), they should be converted to Particle System Emitters.
- **Articulated & Chain Bridges (`DingablesBridge`):**
  Sectional suspension bridges (`DingablesBridge.hie` / `Dingables_Bridge.dcol`) use spline trajectories (`PATH_FOLLOWERS`) or articulation chains (`ARTICULATED_BRIDGES`). The `.hie` contains individual segment geometry, which `TDR2000.exe` replicates along the spline path.

---

## 4. Scene Schema Specification (`scene.json`)

To ensure seamless import into game engines (Unreal, Unity, Godot), level data is split into **Physical Geometry** (`.obj` / `.gltf`) and **Logical Scene Data** (`scene.json`).

### 4.1 Root Schema Specification (`schemaVersion: 1`)

The exporter operates in a **Hybrid RAW + Scene Manifesto** mode:
1. **RAW Geometry Export:** All base meshes, race layers, mission layers, and movables are dumped into clean individual 3D files (`.obj` / `.gltf`).
2. **Scene Manifesto (`scene.json`):** A clean metadata manifesto indexes layers into structured variants (`race1`, `race2`, `race3`, `missions`, `multiplayer`) without hardcoded tags.

```json
{
  "schemaVersion": 1,
  "trackName": "hollowood",
  "baseMesh": "HollowoodMesh.obj",
  "waterMesh": "HollowoodWater.obj",
  "skyMesh": "FilmSkysphereStudio.obj",
  "variants": {
    "race1": {
      "consoft": "hollowoodRace1Consoft.obj",
      "raceline": "FilmStudioRace1.txt"
    },
    "race2": {
      "consoft": "hollowoodRace_2_Consoft.obj"
    },
    "race3": {
      "consoft": "hollowoodRace3_Consoft.obj"
    },
    "multiplayer": {
      "mesh": "HollowoodMultiPlayerMesh.obj",
      "mapMesh": "HollowoodMap_MultiMesh.obj"
    }
  },
  "entities": [],
  "paths": []
}
```

### 4.2 Entity Types (`point` vs `path`)

#### 1. Point Entities (`type: "point"`)
Uses the standard `"Name" X Y Z QX QY QZ QW` tokenizer.
Used for: Movables, Zombie Spawns, Pedestrians, Powerups, Player Grid Start Positions.

```json
{
  "id": "zombie_spawn_001",
  "category": "zombie_spawn",
  "name": "Zombie_Male_01",
  "position": [21.5, 5.2, -170.9],
  "rotation": [0.0, -0.707, 0.0, 0.707],
  "sourceFile": "Hollowood_ZombieDescriptor.txt"
}
```

#### 2. Path & Spline Entities (`type: "path"`)
Used for: Waypoint trajectories (`Level Drones\*.txt`, `PATH_FOLLOWERS`), traffic splines (`.lin`).
Preserves sequential waypoint connectivity.

```json
{
  "id": "drone_path_001",
  "category": "traffic_drone_path",
  "name": "FilmStudioTraffic_Paths_1",
  "closedLoop": true,
  "points": [
    [10.0, 4.0, 50.0],
    [35.0, 4.0, 120.0],
    [80.0, 4.0, 210.0]
  ],
  "sourceFile": "FilmStudioTraffic_Paths_1.txt"
}
```

### 4.3 Environment & Atmosphere Lighting Parameters (From `sub_49C280` Decompilation)

Lighting and fog parameters are read directly from `{TrackName}.txt` level descriptors by engine function `sub_49C280` (using `sub_585AE0` for string tokens and `sub_585BC0` for numeric values).

- **Sun Direction Vector (`SUN_VECTOR`):** Read as 3 floats (`[X, Y, Z]`), stored at memory offset `this + 384`.
- **Ambient Light Intensity (`AMBIENT_LIGHT`):** Read as 1 float intensity, stored at memory offset `this + 376`.
- **Ambient Light Color (`AMBIENT_LIGHT_COLOUR`):** Read as 3 floats (`[R, G, B]`), stored at memory offset `this + 460`.
- **Sun Color (`SUN_LIGHT_COLOUR`):** Read as 3 floats (`[R, G, B]`), stored at memory offset `this + 472`.
- **Track Fog Enable (`FOG_TRACK`):** Integer boolean (`0` or `1`), stored at memory offset `this + 508`.
- **Track Fog Color (`FOG_TRACK_COLOUR`):** Read as 3 floats (`[R, G, B]`), stored at memory offset `this + 512`.
- **Sky Fog Enable (`FOG_SKY`):** Integer boolean (`0` or `1`), stored at memory offset `this + 532`.
- **Sky Fog Color (`FOG_SKY_COLOUR`):** Read as 3 floats (`[R, G, B]`), stored at memory offset `this + 536`.
- **Sky Fog Distances (`FOG_SKY_NEAR` / `FOG_SKY_FAR`):** Float near/far clip distances, stored at memory offsets `this + 548` and `this + 552`.
- **Track Fog Near/Far (`FOG_TRACK_NEAR` / `FOG_TRACK_FAR`):** *Dead Fields (parsed into local stack buffer v136 and discarded by engine).*

#### Environment JSON Schema Specification:
```json
"environment": {
  "sunVector": [-0.577, 0.577, -0.577],
  "sunColor": [1.0, 0.95, 0.8],
  "ambientLight": 0.35,
  "ambientColor": [0.2, 0.2, 0.25],
  "fog": {
    "trackFogEnabled": true,
    "trackFogColor": [0.5, 0.55, 0.6],
    "skyFogEnabled": false,
    "skyFogColor": [0.4, 0.45, 0.5],
    "skyNearDistance": 10.0,
    "skyFarDistance": 350.0
  }
}
```

---

## 5. CLI & Pipeline Standard (`tdr2obj`)

- **Interactive Mode:** Launch without `-l` to scan VFS and select track via menu.
- **Direct CLI Export:** `TDR2OBJ -l <trackName> -a <assetsPath> [-o <outDir>]`
- **Variant Export:** `TDR2OBJ -l <trackName> --variant <variantSuffix> -a <assetsPath>`
  - `--variant race1` / `-var race1` / `-r race1` — loads `{Track}_Race1.txt` descriptor chain
  - `--variant mission1` / `-var mission1` / `-m mission1` — loads `{Track}_Mission1.txt` descriptor chain
  - Short numeric: `-var 1` → tries `{Track}_race1.txt`, then `{Track}_mission1.txt`
- **Flags:**
  - `-v` / `--verbose`: Detailed VFS HIT/MISS & pipeline stage logging.
  - `-ls` / `--list`: VFS archive contents dump.
  - `--no-group` / `-ng`: Disable `g <GroupName>` grouping in OBJ output.
  - `-lc` / `--local`: Output local mesh coordinates with `# WorldPos` comments.

### 5.3 Future GUI Wizard & Visual Tree Explorer (Proposed Concept)

To avoid hardcoded CLI exclusions and give users total control over complex multi-layer tracks:
- **Full Hierarchy Tree View:** Displays all discovered level HIE nodes, race layers (`FilmStudioRace1.hie`), mission variants (`Hollowood_Mission1.hie`), and HUD map planes (`HollowoodMapMesh.hie`).
- **Interactive Toggles:** Checkbox controls to select/deselect specific meshes or overlay layers before export.
- **Dynamic Prop Preview:** Live 3D preview for inspecting movables, spline paths, and checkpoint locations prior to generating OBJ/gLTF/Scene JSON files.

---

## 6. Track Behavior & Selection Investigation Matrix

To guarantee 100% reliable conversion, the export pipeline adheres to strict rules separating **Physical Geometry** (pure 3D meshes) from **Logical Level Selection** (`scene.json` metadata):

### 6.1 Geometry vs. Metadata Separation Principle
1. **Meshes (`.obj` / `.gltf`):** Pure 3D geometry only. No hardcoded game logic, no mission conditions, no coordinate baked transformations.
2. **Metadata (`scene.json`):** Holds all spatial coordinates, instance placements, variant selectors, waypoints, and lighting properties.

### 6.2 Track Variant Resolution Matrix

| Layer Category | Discovery Criteria | `scene.json` Tag | Engine Behavior |
| :--- | :--- | :--- | :--- |
| **Base Terrain** | `{Track}Mesh.hie` | `baseMesh` | Primary world collision & visual terrain. |
| **Sky / Water** | `{Track}Water.hie`, `*Skysphere*.hie` | `environment.skyMesh`, `waterMesh` | Atmosphere models. |
| **Race Variant 1-3** | Fuzzy-match via `ResolveTrackDescriptor`: reads `{Track}_Race{N}.txt` → `STATIC_MESH_DESCRIPTOR` → discovers Consoft `.hie`. Consoft file naming is **inconsistent across tracks** (confirmed from real game verbose log): `hollowoodRace1Consoft.hie`, `hollowoodRace_2_Consoft.hie`, `hollowoodRace3_Consoft.hie`. **Do NOT hardcode a single pattern.** Stage 5 auto-discovery uses `fileName.StartsWith(tName)` as a safety net. | `variants.race{N}.consoft` | Physical checkpoint gantries & arrow props for Race N. |
| **Race Waypoints** | `{Track}Race{N}.txt` / `RACELINE` | `variants.race{N}.raceline` | Sequential waypoint node array `[X, Y, Z]`. |
| **Mission Overlay** | `{Track}_Mission{N}_*.hie` | `variants.mission{N}.overlays` | Mission-specific breakables & target structures. |
| **Multiplayer Variant** | `{Track}MultiPlayer*.hie` | `variants.multiplayer.mesh` | Custom multiplayer layout / stunt park arena. |
| **HUD Radar Planes** | `{Track}MapMesh.hie` | `variants.hud.radarMesh` | 2D HUD radar projections (marked as non-world geometry). |
| **Movable Objects** | `{Track}_MovableDescriptor.txt` | `entities` (category: `movable`) | Dynamic physics props placed via transformation quaternions. |

