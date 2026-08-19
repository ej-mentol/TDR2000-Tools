\# TDR2000 Descriptor \& HIE Format — Exporter Contract



\## Track descriptor (level .txt, e.g. Hollowood.txt)

Line format `KEYWORD pathorvalue \[extra tokens...]`

\- Keyword match is EXACT on first token, not prefix. 

&#x20; (SKY\_SPHERE\_RENDER\_NODE starts with SKY\_SPHERE — must not match SKY\_SPHERE parser)

# TDR2000 Descriptor & HIE Format — Exporter Contract

## Track descriptor (level .txt, e.g. Hollowood.txt)

Line format `KEYWORD pathorvalue [extra tokens...]`

- Keyword match is EXACT on first token, not prefix. 
  (SKY_SPHERE_RENDER_NODE starts with SKY_SPHERE — must not match SKY_SPHERE parser)
- Comments start with `//`, strip everything after on the line.
- Values may be quoted or unquoted; quotes are stripped per-token, never from the raw line.

Known keywords consumed by the exporter (source: Program.cs HasKeyword calls)

  Stage 1 — HIE geometry & environment (direct .hie or .txt sub-descriptor)
  Keyword                   Value type       Notes
  ---------------------------------------------------
  SKY_SPHERE                 .hie path        Skybox mesh
  WATER_MESH                 .hie path        Water surface
  HARDSHADOW_HIE             .hie path        Shadow projection mesh
  BASE_CONSOFT               .hie or .txt     Base collision/soft mesh
  CONSOFT                    .hie or .txt     Generic soft geometry
  LEVEL_MESH                 .hie or .txt     Level base mesh
  STATIC_MESH                .hie or .txt     Generic static mesh

  Stage 2 — Sub-descriptor dispatch (each value is a .txt, recursed into)
  Keyword                   Value type
  ---------------------------------------------------
  STATIC_MESH_DESCRIPTOR     .txt path        → list of .hie entries
  BREAKABLES_DESCRIPTOR      .txt path        → list of .hie entries
  ANIMATED_PROPS             .txt path        → list of .hie entries
  CONSOFT_DESCRIPTOR         .txt path        → Consoft .hie entries
  LEVEL_CONSOFT              .txt path        → Level consoft .hie entries

  Stage 3 — Movable object placements
  Keyword                   Value type
  ---------------------------------------------------
  MOVABLE_OBJECTS            .txt path        → one placement per line: NAME.hie X Y Z QX QY QZ QW


## Cumulative Base Track + Variant Descriptor Inheritance Rule

When processing a track variant descriptor (e.g. `Hollowood_Race1.txt` or `Hollowood_Mission1.txt`):
1. **Base Mesh Geometry**: Static `.hie` meshes are accumulated from BOTH the Base Track descriptor (`Hollowood.txt`) AND the Variant Track descriptor (`Hollowood_Race1.txt`).
2. **Movable Placements (`MOVABLE_OBJECTS`)**: Movable object placement descriptors are accumulated cumulatively (`Hollowood_MoveableDescriptor.txt` + `Hollowood_Race1_MoveableDescriptor.txt`).
3. **Powerup Pickups (`.pup`)**: Powerup files are accumulated cumulatively (`Hollowood.pup` + `Hollowood_Race1.pup`).
4. **Checkpoints & Soft Geometry (`BASE_CONSOFT` / `CONSOFT`)**: Collision/soft checkpoint volumes are accumulated cumulatively (`Hollowood_consoft.txt` + `Hollowood_Race1_consoft.txt`).


Known keywords NOT consumed (present in files, intentionally ignored)

PEDS\_DESCRIPTOR, ZOMBIES\_DESCRIPTOR, ALIENS\_DESCRIPTOR, DRONE\_DESCRIPTOR,

LIGHTS\_DESCRIPTOR, LEVEL\_SCRIPT, TEXTURE\_ANIM\_DESCRIPTOR, PATH\_FOLLOWERS,

AMBIENT\_SOUNDS, RADAR\_DESCRIPTOR, OCCLUDER\_MESH (.scol — not a mesh format we read),

FOG\_, SUN\_, START\_POS, ...



\## Sub-descriptor (.txt referenced by STATIC\_MESH\_DESCRIPTOR  BREAKABLES  ANIMATED\_PROPS)

One entry per line, no keyword prefix

`path.hie` or `path.hie flags... collision\_path.txt`

\- First whitespace-separated token is the entry. Extra tokens (flags, collision

&#x20; descriptor path) are present but currently unused by the exporter.

\- Entry is either .hie (mesh, exported directly) or .txt (nested descriptor, recurse).



\## Movables descriptor (MOVABLE\_OBJECTS target)

One placement per line

`NAME.hie X Y Z QX QY QZ QW`

Position (float), quaternion (float×4). Comments via ``.



\## HIE hierarchy file

Text sections split by ` section name` markers (case-insensitive).

Sections consumed version, texture list, material list, matrix list, mesh list, node list.

Node line `TYPE INDEX CHILD SIBLING` (NULL = -1).

NodeType 1=Matrix 2=Texture 3=Mesh 4=Expression 5=Material 6=Spline 7=DynamicCollision 8=CullNode.



\## Known fragility

\- Matrix name line parsing (HIEParser) has two different paths for consuming an

&#x20; optional quoted name (before vs after the 3 matrix rows) — not a clean invariant,

&#x20; reverse-engineered against real files. If a HIE fails to parse, check here first.

\- Section end detection relies on next line NOT starting with // — an empty
  line inside a list, or missing trailing // marker, will desync parsing.
- Binary Disassembly Offsets & Subroutines: The internal type IDs, subroutines, and offset assumptions documented here were identified from specific builds/versions of the official TDR2000.exe (e.g. Steam/UK release). Other game editions, patches, demos (e.g. 1920s early prototypes/OEM builds) may have different memory offsets, keyword naming, or structure padding.
