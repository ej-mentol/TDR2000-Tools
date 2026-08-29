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
7. [Rest-Pose Skinning Validation & Spike Detection](#7-rest-pose-skinning-validation--spike-detection)
8. [Splines & Road Networks (`.lin` / `.lins`)](#8-splines--road-networks-lin--lins)
9. [Level Descriptors & Mission Scripts (`.txt` / `.opt`)](#9-level-descriptors--mission-scripts-txt--opt)
10. [Powerups & Collision Volumes (`.pup` / `.scol`)](#10-powerups--collision-volumes-pup--scol)
11. [Textures & Materials (`.tga` / `.png`)](#11-textures--materials-tga--png)
12. [3D Export Pipeline (OBJ / glTF / GLB / SceneJSON)](#12-3d-export-pipeline-obj--gltf--glb--scenejson)

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

Used for pedestrians, zombies, creatures, and quadrupeds with skeletal vertex blending.

### File Layout:
```
[Header]
  uint32 name_length
  char   name[name_length]     // Internal species name (e.g. "FLAG_WOMAN", "GIANT_RAT", "ALIEN1", "MAN", "WOMAN", "DOG", "BULL")
  uint32 lod_count             // Typically 1 to 4 LODs

[LOD Parts Stream]
  uint32 polygon_count         // Number of polygons in body part
  uint32 polygon_type          // Polygon vertex count (3 = Triangles, 4 = Quads, 5..10 = N-gons via Fan)
```

### Vertex Layout:
```
float32 pos_x, pos_y, pos_z    // Vertex position (meters)
float32 norm_x, norm_y, norm_z // Vertex normal (sanitized against NaNs/Infs)
uint32  bone_indices           // 4 packed 1-based bone IDs (b0 | b1<<8 | b2<<16 | b3<<24)
float32 weights[bone_count]    // Blend weights (1 to 4 floats, sum normalized to 1.0)
uint32  rgba_color             // Vertex color
float32 u, v                   // 3ds Max UV coordinates (V=0 bottom, V=1 top)
```

### Critical Rules:
1. **1-Based Bone Indexing & Dynamic `num_bones` Clamping:**
   * Indices in `.ski` are 1-based (`1..N`). In 0-based engines: `joint_idx = bone_idx - 1`.
   * Joint indices must be clamped strictly to $[0, \text{num\_bones} - 1]$ based on the exact active bone count of the resolved skeleton (e.g., 12 for `alien3`, 13 for `alien2`/`kanga`, 14 for `sheep`, 18 for `bull`/`cat`, 20 for `dog`/`horse`/`giantrat`, 25 for `man`, 27 for `woman`, 30 for `flag_woman`).
2. **Two-Pass SKI Parsing Pipeline:**
   * **Pass 1 (Probe):** Reads the embedded header name (`FLAG_WOMAN`, `GIANT_RAT`, `ALIEN2`, etc.) to dynamically resolve the matching `.ske` rig with filename stem fallback.
   * **Pass 2 (Final):** Extracts mesh geometry with exact bone count clamping and weight normalization ($\sum w_i = 1.0$).
3. **glTF 2.0 UV V-Axis Inversion ($V_{\text{glTF}} = 1.0 - |V_{\text{ski}}|$):**
   * `.ski` vertex UVs are authored in 3ds Max space ($V=0.0$ at the chin/bottom, $V=1.0$ at the forehead/top).
   * For standard glTF 2.0 rendering (origin $(0,0)$ at top-left), convert UVs via:
     $$V_{\text{glTF}} = 1.0 - |V_{\text{ski}}|$$
   * This permanently fixes upside-down faces and textures across all characters.
4. **Universal Data-Driven LOD 0 Boundary Extraction:**
   * Character models stream body parts sequentially following the kinematic hierarchy (Head $\to$ Torso $\to$ Arms $\to$ Pelvis $\to$ Thighs $\to$ Shins $\to$ Feet down to $Y \approx 0.002$ m).
   * Once lower limbs/feet are reached, the re-appearance of the initial root/head bone set signals the start of LOD 1.
   * Isolating LOD 0 dynamically eliminates overlapping low-poly shells and Z-fighting while guaranteeing 100% of all limbs, legs, and feet are preserved across all humanoids, animals, and aliens.
5. **Dual Material Partitioning (Face vs. Body):**
   * When character descriptors (`PedDescriptor.txt`) specify distinct face and body textures (e.g. `officialface_64_64_32.tga` and `official_128_256_32.tga`), parts bound to head bones (bones 4, 5) are cleanly partitioned into separate PBR material primitives.

---

## 5. Skeletal Armature & Bone Hierarchy (`.ske`)

Defines rest pose bone transformations, parent-child kinematic chains, and joint basis matrices for humanoid and non-humanoid characters.

### Header (4 bytes):
* `uint32 bone_count`: Total active bones $N$ in the model (e.g. 12 for alien3, 13 for alien2/kanga, 14 for sheep, 17 for llama, 18 for bull/cat, 20 for dog/horse/giantrat, 25 for man/zombie, 27 for woman, 30 for flag woman).

### Record Layout (76 bytes per record):
* `float32 matrix[16]`: 4x4 Row-Major matrix (translations in decimeters).
* `uint32 padding`: Always `0`.
* `int32 bone_id`: Unique active bone index ($0 .. N-1$) or `-1` for dummy/helper nodes.
* `uint32 flag`: Scene graph DFS tree token (`2, 3, 4, 8` = Branch push, `1` = Chain node, `0` = Branch pop / return to parent).

### DFS Branch-Stack Kinematic Reconstruction:
* Parses records via a 100% pure binary Depth-First Search branch-stack:
  * **Branch Push (`flag in (2, 3, 4, 8)`):** Assigns parent from current active node, pushes current node onto the stack, and switches active node to new bone ID.
  * **Chain Sequential (`flag == 1`):** Assigns parent from current node and advances active node.
  * **Branch Pop (`flag == 0`):** Completes current leaf chain, pops parent from stack, returning to previous branch fork point (or resets to character root `Bone 0` if stack is empty).
  * **Stack Balance Verification:** All 14 skeleton hierarchies in the game finish with `len(stack) == 0` (100% balanced push/pop balance).

### Least-Squares (МНК) Multi-Joint Scale Calibration:
Rather than relying on a single root translation anchor, translation scale $s$ is calibrated per mesh via Least-Squares regression across all joints with dominant vertex clustering ($w > 0.5$):
$$s = \frac{\sum_i \vec{r}_{\text{raw}, i} \cdot \vec{m}_{\text{mesh}, i}}{\sum_i ||\vec{r}_{\text{raw}, i}||^2}$$
Where $\vec{r}_{\text{raw}, i} = \vec{T}_{\text{raw}, i} - \vec{T}_{\text{raw}, 0}$ and $\vec{m}_{\text{mesh}, i} = \vec{C}_{\text{mesh}, i} - \vec{C}_{\text{mesh}, 0}$.
This completely eliminates single-point measurement noise and prevents accumulated scale drift along distal limb chains (shoulders $\to$ elbows $\to$ wrists $\to$ fingers).

### Orthonormal Unit-Basis Normalization (`scale = 1.0`):
In 3ds Max Biped, helper nub bones (e.g. Bone 10, 15, 18) were exported with non-unit matrix scale ($s \approx 0.0396$). Because 3D engines (Blender/Unity/glTF) strip matrix scale on EditBones in rest pose while retaining $1/s = 25.25\times$ in Inverse Bind Matrices, evaluating $\text{EditBone} \times \text{IBM}$ causes extreme mesh spikes.
Normalizing rotation column basis vectors to strict unit length ($\text{scale} \equiv 1.0$) in `row_to_col_major`:
$$\vec{u}_0 = \frac{\vec{r}_0}{||\vec{r}_0||}, \quad \vec{u}_1 = \frac{\vec{r}_1}{||\vec{r}_1||}, \quad \vec{u}_2 = \frac{\vec{r}_2}{||\vec{r}_2||}$$
ensures $\mathbf{M}_{\text{EditBone}} \cdot \text{IBM} \equiv \mathbf{I}$, permanently eliminating spikes.

### Basis Permutation (3ds Max Biped to glTF 2.0 Bone Space):
```
Col 0 (glTF Local X: Lateral) = Normalized Biped Row 2
Col 1 (glTF Local Y: Length ) = Normalized Biped Row 0
Col 2 (glTF Local Z: Normal ) = Normalized Biped Row 1
Col 3 (glTF Translation    ) = (T_x * s, T_y * s, T_z * s, 1.0)
```

---

## 6. Skeletal Animation Tracks (`.ani`)

Stores keyframe animation clips with bone transformation matrices.

### Header (12 bytes):
```
uint32  num_frames             // Number of keyframes (e.g. 24)
float32 fps                    // Playback frame rate (e.g. 10.0, 12.0, 12.5, 15.0, 24.0, 25.0, 30.0, 50.0, 60.0 FPS)
uint32  num_bones              // Bone track count (e.g. 25)
```

### Exact Byte Alignment:
$$\text{FileSize} = 12 + (\text{num\_frames} \times \text{num\_bones} \times 64)$$
* 399 out of 399 `.ani` files in the game adhere strictly to this formula.

### TRS Sampler Packaging & Rest-Pose Composition:
glTF 2.0 animations replace the local node orientation with sampler curve values. To preserve the anatomical rest angles while applying keyframed delta rotations:
1. **Local Delta Matrix Conversion (DirectX $\to$ glTF):**
   $$\mathbf{R}_{\text{ani}} = \mathbf{S}_z \cdot \mathbf{M}_{\text{ani}}^{\top} \cdot \mathbf{S}_z = \begin{pmatrix} m_0 & m_4 & -m_8 & 0 \\ m_1 & m_5 & -m_9 & 0 \\ -m_2 & -m_6 & m_{10} & 0 \\ 0 & 0 & 0 & 1 \end{pmatrix}$$
2. **Local Frame Composition:**
   $$\mathbf{L}_{\text{frame}}(i) = \mathbf{L}_{\text{rest}}(i) \cdot \mathbf{R}_{\text{ani}}(f, i)$$
   $$\mathbf{Q}_{\text{frame}}(i) = \text{mat3\_to\_quat}(\mathbf{L}_{\text{frame}}(i))$$
3. **Track 0 Priority Sorting:**
   Animation tracks are automatically sorted so that default walking cycles (`*walk*.ani`) or idle loops (`*idle*.ani`) occupy Track #0, preventing sports/acrobatic animations (`baseball_man_run.ani`, `breakdance.ani`) from playing by default upon 3D viewport import.

---

## 7. Dual-Stage Skinning & Animation Validation

The export pipeline incorporates a dual-stage mathematical auditor executed on all exported characters:

### Stage 1: Static Rest-Pose Skinning & Spike Validator
1. Reconstructs world matrices $\mathbf{W}_i$ from node hierarchy and local matrices.
2. Multiplies by Inverse Bind Matrices: $\mathbf{S}_i = \mathbf{W}_i \cdot \mathbf{IBM}_i$.
3. Evaluates skinned vertex position for all $V \in \text{LOD } 0$:
   $$\vec{v}_{\text{skinned}} = \sum_{k=0}^3 w_k (\mathbf{S}_{j_k} \cdot \vec{v}_{\text{base}})$$
4. Calculates rest-pose drift: $\text{Drift} = ||\vec{v}_{\text{skinned}} - \vec{v}_{\text{base}}||$.
5. Flags any vertices exceeding threshold ($> 5\text{ mm}$).
* **Benchmark:** 23 / 23 character models verified with $\le 0.004\text{ mm}$ drift and 0 spikes.

### Stage 2: Dynamic Animation Sampler & Bone Length Rigidity Validator
1. Samples animated TRS curves across keyframes (start, middle, end) directly from the glTF binary buffer for all packaged animation clips.
2. Computes dynamic world positions $\mathbf{W}(f, i)$ for every bone and evaluates parent-child distance $D(f, i) = ||\mathbf{W}(f, i) - \mathbf{W}(f, \text{parent})||$.
3. Verifies bone length invariance against rest distance: $\Delta L = |D(f, i) - L_{\text{rest}}|$.
* **Benchmark:** 1297 / 1297 animation clips verified with $\le 0.0006\text{ mm}$ stretch (100% rigid bone kinematic preservation).

---

## 8. Splines & Road Networks (`.lin` / `.lins`)

Used for AI traffic drone navigation, racing paths, and animated object trajectories.

### Format:
* **`.lins` (Binary) / `.lin` (ASCII):** List of 3D points `[X, Y, Z]` with tangent and curvature parameters.
* **World Space Authoring:** Road splines are authored directly in absolute world coordinates matching terrain elevations ($Y \approx 19.89$ m in DocksMD, $Y \in [49.6, 121.1]$ m in Necropolis).
* **AI Drone Spawning:** The game engine spawns 1 to 2 active vehicles per class along major arterial splines ($\ge 25$ m), reserving short 5-meter segments for junction turns.

---

## 9. Level Descriptors & Mission Scripts (`.txt` / `.opt`)

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

## 10. Powerups & Collision Volumes (`.pup` / `.scol`)

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

## 11. Textures & Materials (`.tx` / `.tga` / `.png`)

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
* **Double-Sided Geometry (`doubleSided`):** Set to `true` across materials in glTF exports to ensure planar fences, signs, and thin panels remain 100% visible regardless of backface culling settings.
* **Texture Fallback Hierarchy:** Tiered resolution (Tier 1A exact PAK $\to$ Tier 1B same directory $\to$ Tier 2 same track/variant $\to$ Tier 3 shared non-track assets $\to$ Tier 4/5 global VFS) with cross-variant isolation.

---

## 12. 3D Export Pipeline (OBJ / glTF / GLB / SceneJSON)

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

---

## 13. Export Architecture & Code Structure (`src/TDR.Tools/Export/`)

The export engine is organized into single-responsibility submodules under `namespace TDR.Tools.Export`:

```text
src/TDR.Tools/Export/
├── Model/                                 # Data transfer objects & schemas
│   ├── Domain/
│   │   ├── SceneEntity.cs                 # PlacedEntity, EntityCategory
│   │   └── ExportResult.cs                # TrackExportResult record
│   └── Gltf/
│       └── GltfModel.cs                   # 18 glTF 2.0 schema DTOs (GltfManifest, GltfNode, etc.)
│
├── Pipeline/                              # High-level reconstruction & orchestration
│   ├── TrackExportPipeline.cs             # Top-level coordinator for multi-format track exports
│   ├── SceneReconstruction.cs             # Entity spawning, ground raycasting, drone capping
│   └── LevelDescriptorParser.cs           # Parsing track descriptor scripts (.txt / .opt)
│
├── Resolvers/                             # Asset matching, geometry reading & format decoders
│   ├── MaterialResolver.cs                # .tx transparency & PBR material state resolution
│   ├── TextureResolver.cs                 # 5-tier VFS texture candidate matching & track isolation
│   ├── SplineResolver.cs                  # Dynamic spline assignment (trains, sharks, planes)
│   ├── MeshGeometryReader.cs              # Fast triangle stream extraction from MSHS containers
│   └── TgaDecoder.cs                      # Raw TGA pixel decoder & transparency detection
│
├── Writers/                               # Low-level file format encoders
│   ├── GltfExporter.cs                    # glTF 2.0 JSON + binary buffer writer
│   ├── ObjExporter.cs                     # Wavefront .obj + .mtl geometry writer
│   └── SceneJsonExporter.cs               # Engine scene manifesto JSON serializer
│
├── Services/                              # Domain services
│   └── TextureResolutionService.cs        # Texture disk saving & PNG conversion service
│
└── Utilities/                             # Spatial acceleration & physics utilities
    └── TerrainRaycaster.cs                # Spatial grid acceleration & Möller-Trumbore raycaster
```

