# Moonlace

A native, Linux-first desktop workbench for Final Fantasy XIV models and Penumbra mods.

Browse gear, accessories, character models, and body parts straight from the game files. Preview them fully textured in 3D, edit models and materials, swap textures, live-edit installed Penumbra mods, retarget existing mods onto other items or emotes, and upgrade older modpacks to Dawntrail formats.

Your actual FFXIV installation stays read-only the entire time.

![platforms](https://img.shields.io/badge/platforms-Linux%20%C2%B7%20Windows-8a7aa8)

![Moonlace](media/moonlace.png)

## Features

* **Browse basically everything**
  Explore around 29,000 gear pieces and accessories, plus faces, hairstyles, tails, and body models. Everything is arranged in a searchable tree under Gear, Accessories, and Body.

* **Real 3D previews**
  Models are rendered with their actual textures, materials, color tables, and race or gender variants.

* **Edit without touching the game files**
  Export models as GLTF or FBX, edit them in Blender, then import them back into Moonlace. Recolor material tables, replace textures, and reassign materials per mesh while seeing the results live.

* **Speaks fluent Blender and TexTools**
  Imports understand the TexTools mesh naming convention (`chest 0.0`, `Part 1-4`, and friends), read Blender object names, and regroup submesh parts automatically. Bone weights survive the round trip.

* **Retarget mods onto other items**
  Take a mod made for one gear piece and rewire it onto a different item, slot, or race and gender combination, then save it as a new standalone PMP. Each affected model can go to its own destination, even across slots.

* **Retarget animation mods onto other emotes**
  Move a custom emote, idle, or sitting pose onto a different emote. Moonlace patches the animation clip names so the destination timeline actually plays them, and saves the result as a new PMP.

* **Live-edit Penumbra mods**
  Link an installed Penumbra mod and edit its files directly. Moonlace respects the mod's option structure and backs up every file before its first change, so the whole session can be reverted later.

* **Create Penumbra options**
  Add new option groups and options from inside Moonlace, then capture your edits into them. New options start empty, allowing the mod's existing files to remain active underneath.

* **Upgrade older mods to Dawntrail**
  Import an Endwalker-era `.pmp`, `.ttmp`, or `.ttmp2` and export an updated `.pmp`. Moonlace converts legacy materials, index textures, masks, and normals to the current game formats, and translates TexTools `.meta` and `.rgsp` metadata into Penumbra manipulations.

* **Export as PMP**
  Package the edits from your current session into a Penumbra mod with one click.

* **Editing sessions**
  Every launch starts a fresh session, so you always begin with a clean worktree. The Sessions menu shows exactly which files you have touched, reconnects you to any previous session, and can automatically clean up sessions you have not used for a day, a week, or a month.

* **FFXIV stays pristine**
  Moonlace treats the game installation as strictly read-only. Normal edits live in Moonlace sessions. Live Penumbra edits only touch the mod folder you explicitly linked. Retargeting and upgrading read the source modpack and write a brand new file.

## Download and run

Grab the build for your platform from the latest release. Every variant keeps itself up to date: when a new version is published, an **Update** button appears in the top bar and one click installs it and restarts Moonlace.

### Linux

Download:

```text
Moonlace.AppImage
```

Then:

```sh
chmod +x Moonlace.AppImage
./Moonlace.AppImage
```

The AppImage updates itself in place.

### Windows

Pick one:

```text
Moonlace-win-Setup.exe      installer with shortcuts
Moonlace-win-Portable.zip   no install, run from anywhere
```

Run the installer, or extract the portable zip and run `Moonlace.exe`. Both update themselves.

The release builds are self-contained, so there is no separate .NET runtime to install.

Linux only needs a working OpenGL driver. Both X11 and Wayland are supported.

## First run

On first launch, Moonlace asks where FFXIV is installed.

You can point it at any of these:

* the FFXIV installation root
* the `game` directory
* the `sqpack` directory

Moonlace validates the path and remembers it for next time.

A typical Steam or Proton installation might look like:

```text
…/steamapps/common/FINAL FANTASY XIV Online/game
```

## Quick tour

### Browsing

The left panel contains three main sections:

* **Gear**
* **Accessories**
* **Body**

Body includes things such as faces, hairstyles, tails, and body models, grouped further by gender and race.

Expand a category manually or type into the search box to filter the entire tree. Search results remain grouped under their original categories.

Select an item and its model appears in the viewport, with the available editing tools shown alongside it. The three columns are draggable, so whichever one is busiest can have the space.

If the item has multiple race or gender variants, use the **Model version** dropdown to switch between them.

### Viewport controls

* Drag to rotate
* Right-drag to pan
* Scroll to zoom

Tiny digital dress-up doll controls. Very serious software.

### Model editing

The **Model** tab lets you export and import GLTF and FBX files for Blender round-tripping.

Bone weights are included, and imported models can be previewed immediately.

Exports name each submesh part (`mesh_2.1` is mesh 2, part 1) so the FFXIV partition is visible and editable in Blender. On import, only the trailing numbers of a mesh name matter, so TexTools-style names such as `chest 0.0` work exactly as you would expect, and Blender object names are read too.

You can also reassign meshes to any material available on the model.

### Material editing

The **Material** tab exposes the model's material data and color tables.

You can edit values such as:

* diffuse color
* specular color
* emissive color
* gloss

Texture slots can also be redirected to different textures.

### Texture editing

The **Texture** tab lets you preview, export, and replace textures.

Textures export as PNG.

Imports support:

```text
PNG
JPG
TGA
BMP
```

### Edit sessions

Normal edits are stored per item and persist across restarts.

Use **Discard changes** to throw away the session and return to the untouched game assets.

Use **Export PMP...** to package the current edits into a Penumbra mod.

## Penumbra live editing

Open:

```text
Penumbra → Live edit...
```

Then select an installed Penumbra mod folder.

Moonlace loads the mod's option structure and lets you choose which options should be active while editing.

From there, edit the model, materials, or textures normally.

The difference is that changes are written directly into the linked mod folder. Redraw the character in Penumbra and the results can be checked in game immediately.

Before Moonlace changes a file for the first time, the original is copied into:

```text
.moonlace-backup/
```

These backups survive restarts.

Use **Revert changes** to restore the original files from the current live-edit session.

### Creating new mod options

Use:

```text
New option or group...
```

to add new Penumbra option groups or options.

New options begin empty. Existing mod files continue to provide their defaults underneath them.

Edits made while the new option is active can then be captured into that option, after which Penumbra can toggle it normally.

## Retargeting mods

The **Mod tools** menu hosts tools that work modpack-to-modpack: they read a `.pmp`, `.ttmp`, or `.ttmp2`, and write a new standalone `.pmp`. The source file is never modified.

### Retarget mod

```text
Mod tools → Retarget mod...
```

Pick a modpack and Moonlace shows every model it affects, labeled with the item names that use it. Give each one a destination item and a race or gender, or leave it unchanged.

Moonlace remaps the paths, patches the model's material references, and pulls in any game materials the destination needs, so the retargeted model resolves completely. Bindings can move across slots, and even between gear and accessories, though meshes keep their original rigging, so a necklace worn as a coat may sit a little creatively.

### Retarget animation

```text
Mod tools → Retarget animation...
```

Pick an animation modpack (a custom emote, idle, or sitting pose) and choose a new emote or animation for each part of it. The searchable destination list covers every emote, complete with slash commands, plus friendly names for the nameless sitting pose families.

The tricky part happens automatically: animation files reference their clips by name, so Moonlace patches the clip names to match the destination, letting the destination's own timeline pick them up. Edited timelines travel along with their animations so the pair stays consistent.

## Upgrading mods to Dawntrail

Open:

```text
Files → Upgrade to DT...
```

Moonlace accepts:

```text
.pmp
.ttmp
.ttmp2
```

and writes a new upgraded `.pmp` next to the original file.

The source modpack is never modified.

During conversion, Moonlace can update:

* pre-Dawntrail material formats
* index textures
* mask channels
* normal channels

TexTools `.meta` and `.rgsp` metadata entries are translated into Penumbra manipulations, so IMC, EQP, EQDP, EST, GMP, and racial scaling edits survive the trip into the `.pmp`.

It also works as a straightforward TTMP to PMP converter when the source pack already uses current formats.

## Data locations

| What              | Linux                                     | Windows                                   |
| ----------------- | ----------------------------------------- | ----------------------------------------- |
| Settings          | `~/.config/Moonlace/`                     | `%APPDATA%\Moonlace\`                     |
| Edit sessions     | `~/.local/share/Moonlace/sessions/`       | `%LOCALAPPDATA%\Moonlace\sessions\`       |
| Live-edit backups | `.moonlace-backup/` inside the linked mod | `.moonlace-backup/` inside the linked mod |

## Building from source

Moonlace requires the .NET 10 SDK when building from source.

Run the application:

```sh
dotnet run --project src/Moonlace.App
```

Run the tests:

```sh
dotnet test
```

Build release packages into `dist/releases/` (requires the Velopack CLI, `dotnet tool install -g vpk`):

```sh
scripts/build-release.sh
```

## A small but important promise

Moonlace does not modify your FFXIV installation.

Game assets are treated as source material only. Your experiments stay in Moonlace sessions or inside Penumbra mods you deliberately chose to edit.

Break the model, make the texture radioactive pink, give a miqote a deeply questionable material setup, then discard the session and carry on. The game files remain blissfully unaware.
