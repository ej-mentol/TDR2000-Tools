# Carmageddon: TDR 2000 — File Formats & Engine Technical Reference

This document provides a comprehensive technical reference for all binary and text formats used in Carmageddon: TDR 2000, including archive containers, geometry structures, skeletal animation systems, level descriptors, and texture encoding.

---

## Table of Contents

1. [Virtual File System (`.dir` / `.pak`)](#1-virtual-file-system-dir--pak)
2. [Scene Hierarchy & Nodes (`.hie`)](#2-scene-hierarchy--nodes-hie)
3. [Static Mesh Containers (`.mshs` / `.msh`)](#3-static-mesh-containers-mshs--msh)
4. [Skinned Character Meshes (`.ski`)](#4-skinned-character-meshes-ski)
5. [Skeletal Armature & Bone Hierarchy (`.ske`)](#5-skeletal-armature--bone-hierarchy-ske)
6. [Skeletal Animation Tracks (`.ani`)](#6-skeletal-animation-tracks-ani)
7. [Splines & Road Networks (`.lin` / `.lins`)](#7-splines--road-networks-lin--lins)
8. [Level Descriptors & Mission Scripts (`.txt` / `.opt`)](#8-level-descriptors--mission-scripts-txt--opt)
9. [Powerups & Collision Volumes (`.pup` / `.scol`)](#9-powerups--collision-volumes-pup--scol)
10. [Textures & Materials (`.tga` / `.png`)](#10-textures--materials-tga--png)
11. [3D Export Pipeline (OBJ / glTF / GLB / SceneJSON)](#11-3d-export-pipeline-obj--gltf--glb--scenejson)

---

## 1. Virtual File System (`.dir` / `.pak`)

TDR 2000 stores assets in paired Trie directory index files (`.dir`) and data pack containers (`.pak`).

### Structure:
* **`.dir` (Trie Index Header):**
  * Binary prefix-tree indexing uncompressed (`RAW`) and compressed (`zIG` / zlib) file offsets.
  * Node offsets point directly to stream positions within `.pak`.
* **`.pak` (Binary Container):**
  * Contains contiguous file payloads.
  * Payloads starting with `zIG\x00` are standard zlib-compressed streams.
  * Files without header markers are raw byte streams.
* **Precedence Rule:** Loose files located in the game folder on disk override packed `.pak` entries.

---

## 2. Scene Hierarchy & Nodes (`.hie`)

Hierarchical model files (`.hie`) define the transform scene graph, material definitions, texture bindings, and mesh instances for vehicles, level chunks, and props.

### Versions:
* **Version 2 (ASCII):** Standard hierarchy structure.
* **Version 3 (ASCII):** Extended format including collision mesh definitions (`CollisionDataMeshes`).
* **Version 257 (`0x0101` Binary):** Contains binary node graph and an explicit animation playback frame rate (defaults to 60.0 FPS).

### Node Types:
| Type ID | Enum Name | Description |
| :---: | :--- | :--- |
| `1` | `Matrix` | 4x4 coordinate transform / pivot node |
| `2` | `Texture` | Active texture index switch |
| `3` | `Mesh` | Reference index into `.mshs` geometry list |
| `4` | `Expression` | Mathematical motion expression |
| `5` | `Material` | Color / lighting material parameters |
| `6` | `Spline` | Path follower reference |
| `7` | `DynamicCollision`| Rigid body collision hull (`.dcol`) |
| `8` | `CullNode` | Visibility culling bounding volume |

---

## 3. Static Mesh Containers (`.mshs` / `.msh`)

Stores rigid 3D models for terrain, race tracks, buildings, vehicles, and props.

### Mesh Modes (`MeshMode`):
* `NGon = 0` (`0x0000`): Convex polygon face lists.
* `TriIndexedPosition = 256` (`0x0100`): Shared vertex position pool with indexed triangles.
* `Tri = 512` (`0x0200`): Explicit triangle streams.

### Vertex Attributes:
* Vertex Position `(X, Y, Z)` in meters.
* Vertex Normal `(NX, NY, NZ)` (computed from face winding if absent).
* Texture Coordinates `(U, V)`.
* Vertex Color `(R, G, B, A)`.

---

## 4. Skinned Character Meshes (`.ski`)

Used for pedestrians, zombies, and animals with skeletal vertex blending.

### File Layout:
```
[Header]
  uint32 name_length
  char   name[name_length]
  uint32 lod_count             // Typically 1 to 4 LODs

[LOD Parts Stream]
  uint32 polygon_count         // Number of polygons in body part
  uint32 polygon_type          // Polygon vertex count (3 = Triangles, 4 = Quads, 5..10 = N-gons via Fan)
```

### Vertex Layout:
```
float32 pos_x, pos_y, pos_z    // Vertex position (meters)
float32 norm_x, norm_y, norm_z // Vertex normal
uint32  bone_indices           // 4 packed 1-based bone IDs (b0 | b1<<8 | b2<<16 | b3<<24)
float32 weights[bone_count]    // Blend weights (1 to 4 floats)
uint32  rgba_color             // Vertex color
float32 u, v                   // Texture coordinates (0,0 = top-left)
```

### Critical Rules:
1. **1-Based Bone Indexing:** Indices in `.ski` are 1-based (`1..N`). In 0-based engines, subtract 1: `joint_idx = bone_idx - 1`.
2. **Weight Normalization:** Always normalize weights to $1.0$:
   $$w_i = \frac{w_i}{\sum_{k=0}^3 w_k}$$
3. **Universal Data-Driven LOD 0 Boundary Extraction:**
   * Character models stream body parts sequentially following the kinematic hierarchy (Head $\to$ Torso $\to$ Arms $\to$ Pelvis $\to$ Thighs $\to$ Shins $\to$ Feet down to $Y \approx 0.002$ m).
   * Once lower limbs/feet are reached, the re-appearance of the initial root/head bone set signals the start of LOD 1.
   * Isolating LOD 0 dynamically eliminates overlapping low-poly shells and Z-fighting while guaranteeing 100% of all limbs, legs, and feet are preserved across all humanoids, animals, and aliens.

---

## 5. Skeletal Armature & Bone Hierarchy (`.ske`)

Defines rest pose bone transformations and parent-child kinematic chains for characters, animals, and creatures.

### Header (4 bytes):
* `uint32 bone_count`: Total active bones $N$ in the model (e.g. 14 for sheep, 18 for bull, 20 for horse, 25 for man, 27 for woman, 30 for flag woman).

### Record Layout (76 bytes per record):
* `float32 matrix[16]`: 4x4 Row-Major matrix (translations in decimeters).
* `uint32 padding`: Always `0`.
* `int32 bone_id`: Unique active bone index ($0 .. N-1$) or `-1` for dummy/helper nodes.
* `uint32 flag`: Scene graph tree token (`2` = Branch start, `1` = Chain node, `0` = Leaf node, `8` = Root).

### Scale Conversion:
* Convert decimeters to meters by multiplying translation components by $0.1$:
  $$T_{meters} = T_{raw} \times 0.1$$

### Universal Data-Driven Extraction:
Every `.ske` file defines exactly $N$ active bones. Each bone $k \in [0 .. N-1]$ corresponds strictly to the record where `bone_id == k`, mapping 1-to-1 with 1-based vertex blend indices (`joint = b0 - 1`) across all humans, animals, and aliens.

---

## 6. Skeletal Animation Tracks (`.ani`)

Stores keyframe animation clips at a fixed playback speed of 25.0 FPS.

### Header (12 bytes):
```
uint32  num_frames             // Number of keyframes (e.g. 24)
float32 fps                    // Playback speed (always 25.0f)
uint32  num_bones              // Bone track count (e.g. 25)
```

### Exact Byte Alignment:
$$\text{FileSize} = 12 + (\text{num\_frames} \times \text{num\_bones} \times 64)$$
* 401 out of 401 `.ani` files in the game adhere strictly to this formula.

### DirectX (Left-Handed) to glTF (Right-Handed) Conversion:
To ensure human knees bend backward and walking steps move forward, invert the $Z$-axis and $Z$-rotation components:
$$\Delta M_{glTF} = \begin{bmatrix}
 m_{0} &  m_{4} & -m_{8} & 0 \\
 m_{1} &  m_{5} & -m_{9} & 0 \\
-m_{2} & -m_{6} &  m_{10} & 0 \\
 m_{12} &  m_{13} & -m_{14} & 1
\end{bmatrix}$$

---

## 7. Splines & Road Networks (`.lin` / `.lins`)

Used for AI traffic drone navigation, racing paths, and animated object trajectories.

### Format:
* **`.lins` (Binary) / `.lin` (ASCII):** List of 3D points `[X, Y, Z]` with tangent and curvature parameters.
* **World Space Authoring:** Road splines are authored directly in absolute world coordinates matching terrain elevations ($Y \approx 19.89$ m in DocksMD, $Y \in [49.6, 121.1]$ m in Necropolis).
* **AI Drone Spawning:** The game engine spawns 1 to 2 active vehicles per class along major arterial splines ($\ge 25$ m), reserving short 5-meter segments for junction turns.

---

## 8. Level Descriptors & Mission Scripts (`.txt` / `.opt`)

Text and bytecode files defining level parameters, spawners, weather, and mission logic.

### Master Level Descriptor (`{TrackName}.txt`):
* `STATIC_MESH_DESCRIPTOR`: Base environment `.hie`.
* `WATER_MESH`, `WATER_LEVEL`: Water plane geometry and height.
* `MOVABLE_OBJECTS`: Movable props descriptor.
* `DRONE_DESCRIPTOR`: Traffic vehicle allocation pool.
* `PEDS_DESCRIPTOR`: Pedestrian spawner placement list (`*_Ped_Placement.txt`).
* `FOG_TRACK`, `FOG_TRACK_COLOUR`, `FOG_TRACK_DIST`: Atmospheric fog.

### Pedestrian Placement Format (`*_Ped_Placement.txt`):
```
// Type | Skin Type | Standard Ani | Pos(x, y, z)          | Dir(deg)
1        6           -1             -218.96 2.21 33.90       72.76
```
* **`Type`:** Behavior archetype / group ID.
  * `0`: Standard ambient pedestrian (active default wanderer).
  * `1`, `2`, `4`, `5`: Group member, stationary spectator, flee behavior, or mission-specific variants.
* **`Skin Type`:** 0-based index into `PedDescriptor.Textures[]`. The texture entry maps to `PedDescriptor.SkinMeshes[]` (`.ski`) via `PedSkinTexture.SkinIndex` and resolves body/face bitmaps.
* **`Standard Ani`:** Default keyframe animation index (`-1` = default idle/walk).
* **`Pos(x, y, z)`:** World coordinate spawn position.
* **`Dir(deg)`:** Heading angle in degrees (clockwise around Y-axis).

### Mission Pedestrian Target Tracking:
* `CREATE_PEDS <skin_type> <count>`: Spawns specific VIP target pedestrians (e.g. boss Strike Brown).
* `SET_OBJECTIVE_CAMERA CLOSESTPEDPOS DUMMY`: Locks the player's 3D HUD navigation compass arrow directly to the target pedestrian.
* `CREATE_EFFECT EFF_ARROW` / `CREATE_POWERUP <MarkerName>`: Attaches a floating 3D objective arrow over the target.
* `TEST_PED_COUNT <skin_type> 0`: Verifies target elimination before advancing mission state.

---

## 9. Powerups & Collision Volumes (`.pup` / `.scol`)

### Powerup Placements (`.pup`):
* Text files specifying pick-up item locations across the map.
* **Structure:**
  * Comments (`// PowerupName`) and numeric type IDs define the current icon class (`Armor`, `Spanner`, `Time`, `Cash`, etc.).
  * Coordinate rows specify 3D spawn positions `px py pz`.
* **State Isolation & Deduplication:**
  * Exporter parsing maintains per-file name/type state to prevent cross-file bleeding when merging `{Track}.pup` and `{Track}_Race1.pup`.
  * Spatial deduplication ($\text{Distance}^2 < 0.25\,\text{m}^2$) prevents identical pickup icons from overlapping.

### Movable Props Offset (`MoveableDescriptor.txt`):
* Stored as `"ModelName.hie" px py pz qx qy qz qw`.
* Visual mesh pivot sits at the ground contact point.
* Rigid body center of mass is shifted downward by structural delta $\Delta Y$ (e.g. $-0.60$ m for cars, $-1.50$ m for buses, $-0.50$ m for crates).

---

## 10. Textures & Materials (`.tx` / `.tga` / `.png`)

### Native Texture Descriptors (`.tx` / `TTEX`):
TDR2000 uses binary `.tx` descriptors (`TTEX` signature) to define texture LODs, mipmaps, and native DirectX blend states. The 3rd 32-bit integer in the header (`Flags`) serves as the official engine authority for transparency:
* **`Flags = 0` (`OPAQUE`):** Solid terrain, tarmac, rock, buildings, concrete walls. Prevents perforated ground rendering.
* **`Flags & 1` (`BLEND`):** True alpha blending with smooth transparency (e.g. `Water256 copy.tx`, `WaterSplash.tx`, `CarShadow.tx`, light beams, glass).
* **`Flags & 2` (`MASK`):** 1-bit alpha cutout with $0.5$ cutoff threshold (e.g. `FenceWire.tx`, iron bars `Bars.tx`, ladders `Ladder.tx`, railings `Rail.tx`, foliage).

### Material Resolution Hierarchy:
1. **Primary Authority (`TTEX`):** Official `.tx` descriptor loaded directly from the PAK archive or track context.
2. **Secondary Authority (TGA Alpha Byte Inspection):** When `.tx` is not present, `TgaDecoder.DetectTgaTransparency` inspects the raw TGA pixel bytes directly:
   * Intermediate alpha values ($0 < \alpha < 255$) $\to$ `BLEND`.
   * Binary alpha values ($\alpha = 0$ cutout and $\alpha = 255$) $\to$ `MASK` (0.5 cutoff).
   * 24/16-bit or solid $\alpha = 255$ $\to$ `OPAQUE`.
3. **Additive PBR Modifiers:** Unlit/emissive boosts applied for coronas, glows, flares, and sky domes.

* **TGA Support:** 32-bit RGBA (alpha transparency), 24-bit RGB, 16-bit ($1555$, $565$, $4444$), 8-bit paletted, and RLE-compressed (Types 9 and 10).
* **UV Coordinate Origin:** $(0, 0)$ is Top-Left in DirectX / TDR2000 and glTF 2.0. No vertical inversion ($1.0 - V$) is required for glTF exports.
* **Double-Sided Geometry (`doubleSided`):** Set to `true` across materials in glTF exports to ensure planar fences, signs, and thin panels remain 100% visible regardless of backface culling settings.
* **Texture Fallback Hierarchy:** Tiered resolution (Tier 1A exact PAK $\to$ Tier 1B same directory $\to$ Tier 2 same track/variant $\to$ Tier 3 shared non-track assets $\to$ Tier 4/5 global VFS) with cross-variant isolation.

---

## 11. 3D Export Pipeline (OBJ / glTF / GLB / SceneJSON)

### 1. Wavefront `.obj` + `.mtl`:
* Clean static mesh output with material library references (`usemtl PedestrianMat`, `map_Kd`).
* Supports grouping by class for bulk selection in 3D editors.

### 2. Standard glTF 2.0 & Universal Binary `.glb`:
* **Rigid FK Armature:** Hierarchical parent-child bone graph (`Armature` $\to$ `Pelvis` $\to$ `Spine` $\to$ `Chest` $\to$ `Limbs`).
* **Inverse Bind Matrices:** Exact inverse of world bind matrices canceling out rest pose shifts.
* **PBR Metallic Roughness:** Standard roughness factor $0.8$, metallic $0.0$, straight RGBA textures.
* **Embedded Multi-Animation Packs:** Stores multiple actions (`Walk`, `RunHat`, `Breakdance`, etc.) in a single compact binary GLB container.

### 3. Scene Manifest (`scene.json`):
* Complete level metadata, camera angles, lighting vectors, spline waypoints, and object instance transforms.
