<img src="assets/icon.svg" width="64" height="64" alt="HopperFormat">

# HopperFormat

Format the current Grasshopper selection into a left-to-right dataflow. Add a
**Hopper Format** component from **Params > Util**, select components or floating
parameters, focus the canvas, and tap **F twice**. The first tap previews the
selection count; the second formats it. The component's **Format selection**
context-menu item runs the same command.

FF formats selected components and floating parameters, including sliders, panels,
toggles, relays, and opaque clusters. Selected groups include their descendants.
It never selects a containing group, uses a group under the cursor, or formats the
document. Loose scribbles, markup, and the HopperFormat component are reported in
Log as skipped. Nodes with empty bounds after layout are reported and excluded.
With no formattable selection, Status says `Select components to format`.

Wires determine columns and ordering, including hidden wires. Incoming and outgoing
wires to objects outside the selection provide alignment hints. Widget parameters
without sources stack near their consumers. Option tags other than `[lock]` and
continuous formatting remain deferred.

Floating input parameters with an external source and internal consumers stay in
the first column. Floating output parameters with internal sources and no internal
consumers share the island's last column, even without wires leaving the selection.
Panels stay near what they display; source-free widgets still pull toward their
consumers. The solver's loose mode (`Options.ReturnConstraint = false`) disables
both param rules along with the terminal outbound-wire constraint.

Any selected node belonging to a group whose nickname contains **[lock]**,
case-insensitively, blocks the entire run. Membership through nested groups counts.
The diagnostic names the locked group, and nothing moves.

Each successful run with moves creates **one undo record**, named `Format selection`
or `Format groups` when the selection contains a group.
Undo captures the original pivots before moving anything. Applied pivots are
verified; a failed apply restores all original pivots and records no undo event.
An already formatted selection reports `Already formatted` and creates no record.
Formatting does not recompute component geometry; output updates are scheduled.

## Formatting groups

Select one or more groups and tap **FF**. One selected group formats its contents
bottom-up. Several groups and loose selected nodes are also arranged by dataflow,
with group boxes separated by 50 units horizontally and 40 vertically. Nested real
groups move as rigid blocks; scribbles and markup inside them keep their offsets
and translate with their group.

A label group containing exactly one node, transitively, is see-through: its node
uses the ordinary layout rules and its label follows. Overlapping membership in
unrelated groups refuses the run and names the object. A selected **[lock]** group,
a node inside one, or a locked descendant blocks formatting without moving anything.

**Format all** (FF with nothing selected, formatting every group) is coming later. After formatting groups
managed by HopperGroup, tap **Refresh** on HopperGroup; its automatic handshake is
also coming later.

## Component

Only one enabled, unlocked HopperFormat component controls each document. Additional
components report that they are inactive; control transfers if the owner is disabled
or removed.

| Input | Default | Meaning |
| --- | --- | --- |
| Enabled | true | Enable the formatter and FF shortcut |
| Gap X | 30 | Horizontal gap between columns, in canvas units |
| Gap Y | 16 | Vertical gap between nodes |
| Stack Gap | 6 | Gap between widgets feeding the same consumer |
| Debug | false | Include solver log lines in Log |

Gaps must be finite and non-negative; invalid values refuse the run with a diagnostic.

| Output | Meaning |
| --- | --- |
| Status | Current prompt or last result |
| Moves | Number of objects moved by the last run |
| Log | Text list of diagnostics and skipped objects, plus solver details with Debug on |

FF is captured only while the canvas is focused and idle, without modifiers or
mouse buttons. Typing, canvas navigation bindings, and menu shortcuts retain their
normal behavior. Both F taps are consumed; a held key does not count as two taps.

## Requirements

- Rhino **8.20+**, running **.NET 8**, with Grasshopper.
- On Windows, if Rhino runs on .NET Framework, run **SetDotNetRuntime**, choose
  **.NET Core**, and restart Rhino.
- The **FF shortcut is Windows-only**. The **Format selection menu item works
  everywhere** Grasshopper runs on .NET 8.
- After formatting members of groups managed by **HopperGroup**, tap **Refresh**
  on HopperGroup before continuing to drag members. The automatic handshake is
  coming in a later stage.

## Build and test

Use the .NET 8 SDK or newer. The `.slnx` solution requires an SDK with `.slnx`
support (for example .NET 10). Building the project directly does not require it.

```powershell
dotnet build HopperFormat.csproj -c Release
dotnet run --project tests/HopperFormat.SolverTests -c Release
dotnet run --project tests/HopperFormat.ShortcutTests -c Release
```

The plugin builds `bin/Release/net8.0-windows/HopperFormat.gha` and
`bin/Release/net8.0/HopperFormat.gha`. Use the Windows build on Windows and the
plain .NET 8 build on macOS. The shortcut tests require Windows; they use an
off-screen real WinForms control and native window messages without launching Rhino.
The solver tests run without Grasshopper or Rhino. The shortcut project also
exercises snapshot, lock, apply, and session contracts using small Grasshopper
stand-ins; these checks complement the build against the real Grasshopper SDK.

To build every project through the solution:

```powershell
dotnet build HopperFormat.slnx -c Release
```

Regenerate the 24×24 plugin and Yak icons from the SVG design on Windows with
`./tools/GenerateIcon.ps1`.

## Install locally

Close Rhino first. From the repository root in **Git Bash on Windows**, build,
test, package, and install locally with:

```bash
./scripts/release-yak.sh 0.1.0 --allow-dirty --install
```

The script replaces an already installed copy of the same version so a rebuilt
package takes effect. `--install` refuses to replace an installed copy while
Rhino is running. Omit `--install` to build the package only, in
`artifacts/yak/`. The script never publishes unless `--push-test` or
`--push-public` is passed; neither push flag can be combined with `--install`.

To install the built package manually from PowerShell, run this command from
the repository root, using the artifact folder as the source:

```powershell
& "C:\Program Files\Rhino 8\System\Yak.exe" install --source "$PWD\artifacts\yak" hopperformat 0.1.0
```

Restart Rhino and open Grasshopper after installation to load the new build.
On Windows, Rhino stores the package under
`%APPDATA%\McNeel\Rhinoceros\packages\8.0\hopperformat`.
To remove it, close Rhino and run `yak uninstall hopperformat`, or use the full
executable path in PowerShell:

```powershell
& "C:\Program Files\Rhino 8\System\Yak.exe" uninstall hopperformat
```

For a manual reinstall of the same version, uninstall it first, then run the
install command again. Restart Rhino afterward.

MIT license, Copyright (c) 2026 AEC Tooling.
