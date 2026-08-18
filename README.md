# Moonlace

A native, Linux-first desktop workbench for Final Fantasy XIV models and
Penumbra mods. Browse every piece of gear, accessory and character body part
in the game, view it fully textured in 3D, edit models, materials and
textures, live-edit your installed Penumbra mods, and upgrade old modpacks
to Dawntrail — all without ever touching your game installation.

![version](https://img.shields.io/badge/version-2.2-b87f97)
![platforms](https://img.shields.io/badge/platforms-Linux%20%C2%B7%20Windows-8a7aa8)

![Moonlace](media/moonlace.png)

## Features

- **Browse everything** — all ~29,000 gear pieces and accessories plus
  faces, tails and body models, organized in a searchable, collapsible tree
  (Gear · Accessories · Body).
- **True 3D preview** — models render with their real color tables,
  textures and materials, including race/gender variants.
- **Edit non-destructively** — export a model to GLTF, sculpt it in
  Blender, import it back; recolor material color tables; swap textures;
  reassign materials per mesh. Everything previews live in the viewport.
- **Live edit Penumbra mods** — link an installed mod and edit it directly
  in its folder, with the mod's options respected. Every file is backed up
  before its first change, so one click reverts the whole session. You can
  even author new option groups and options, and capture your edits into
  them.
- **Upgrade to Dawntrail** — feed it an Endwalker-era `.pmp` or
  `.ttmp`/`.ttmp2` modpack and get back an upgraded `.pmp`: legacy gear
  materials, index textures, masks and normals are all converted to the
  current game's formats.
- **Export as PMP** — package your session edits as a Penumbra mod with one
  click.
- **Your game stays untouched** — Moonlace treats the FFXIV installation as
  strictly read-only, always. Edits live in per-item sessions or in the mod
  folder you explicitly linked.

## Download & run

Grab the archive for your platform from the release, extract it anywhere,
and run it:

- **Linux** — `Moonlace-2.2.0-linux-x64.tar.gz`, then:

  ```sh
  tar -xzf Moonlace-2.2.0-linux-x64.tar.gz
  cd Moonlace-2.2.0-linux-x64
  ./Moonlace
  ```

- **Windows** — `Moonlace-2.2.0-win-x64.zip`, extract, run `Moonlace.exe`.

The builds are self-contained: no .NET runtime or other dependencies to
install. On Linux you need a working OpenGL driver (any desktop with
X11 or Wayland is fine).

## First run

Moonlace asks for your FFXIV installation once — point it at the game root,
the `game` directory, or the `sqpack` directory; all three work and the
choice is validated and remembered. A typical Steam/Proton path looks like:

```text
…/steamapps/common/FINAL FANTASY XIV Online/game
```

## Quick tour

**Browsing.** The left panel groups everything into *Gear*, *Accessories*
and *Body* (faces, tails, bodies). Click a header to expand it, or just
type in the search box — matches appear under their categories. Selecting
an item shows its model on the right and its editing tabs in the middle.
If an item exists for several races, the *Model version* dropdown switches
between them.

**Viewport controls.** Drag to rotate · right-drag to move · scroll to
zoom.

**Editing.** The *Model* tab exports/imports GLTF (Blender round-trips
cleanly, bone weights included) and lets any mesh use any of the model's
materials. The *Material* tab edits color-table rows — diffuse, specular,
emissive, gloss — and re-points texture slots. The *Texture* tab previews,
exports (PNG) and imports (PNG/JPG/TGA/BMP) textures. Edits are saved per
item and survive restarts; *Discard changes* returns to the pristine game
assets, and *Export PMP…* packages them as a Penumbra mod.

**Penumbra live edit.** *Penumbra → Live edit…* links an installed mod
folder. Pick which of the mod's options you want active, then edit as
usual — changes go straight into the mod's files, so a *Redraw* in
Penumbra shows them in game. The first edit of each file backs the
original up; *Revert changes* restores everything, even across restarts.
*New option or group…* creates fresh options that start empty (the mod's
default files stay active underneath) and captures your edits into them,
so the option can be toggled in Penumbra like any other.

**Upgrade to Dawntrail.** *Files → Upgrade to DT…* takes a `.pmp` or
`.ttmp`/`.ttmp2` modpack and writes a new upgraded `.pmp` next to it —
the original file is never modified. Materials still on the pre-Dawntrail
format are converted, index textures are generated, and mask/normal
channels are moved to the current layout. It also works as a plain
ttmp → pmp converter for packs that are already current. (`.meta`/`.rgsp`
metadata entries are not carried over; import the original alongside if a
pack relies on them.)

## Data locations

| What | Where (Linux) | Where (Windows) |
|---|---|---|
| Settings | `~/.config/Moonlace/` | `%APPDATA%\Moonlace\` |
| Edit sessions | `~/.local/share/Moonlace/sessions/` | `%LOCALAPPDATA%\Moonlace\sessions\` |
| Live-edit backups | `.moonlace-backup/` inside the linked mod | same |

## Building from source

Requires the .NET 10 SDK.

```sh
dotnet run --project src/Moonlace.App    # run from source
dotnet test                              # run the test suite
scripts/build-release.sh                 # build release archives into dist/
```
