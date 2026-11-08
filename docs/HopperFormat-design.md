# HopperFormat: format a group on demand, read it as a function

Design proposal, revised 2026-11-08. Author: tomoS (aectooling). Status: phase 1 (selection-only FF) shipped in PR #1; phase 2 (groups, format all) specified in 5.9 and being built.

## 1. Thesis

A Grasshopper group should read like one small function: all the logic for one job, packed tight, inputs on the left, result on the right. Today that layout is hand-made and decays with every edit.

HopperFormat is a formatter you run, not a rule that runs itself. Tap **F twice** and the selection, including any selected groups, is re-laid out as a dataflow; with nothing selected, FF formats every group in the document: columns by rank, rows chosen to untangle wires, widgets pressed against their consumers, one undo record. Nothing moves unless you ask.

An explicit trigger is the whole point. A formatter that fires on its own after every drop has to reason about which drag is in progress, which plugin saw the drop first, and whether HopperGroup will read its move as the user leaving a group. A formatter that fires on FF never conflicts with a drag, with HopperGroup, or with HopperDraw, because it runs when nothing else is happening and leaves one clean record behind. Continuous enforcement is kept as a later, opt-in extension, and only after the HopperGroup handshake in section 6 exists.

## 2. Why a new plugin

| Plugin | Derives | From |
|---|---|---|
| HopperGroup | membership | position |
| HopperFormat | position | membership + wires |

These are inverse functions. One plugin owning both would couple a drag handler to a layout solver. Keeping them apart, with one explicit handshake between them, matches how the existing plugins already compose, one concern each.

What it borrows, and from where:

- **HopperGroup**: the canvas shortcut hook (generalised from its G-only message handling), the document session registry, the `[tag]` convention in group nicknames, and the headless regression test style.
- **HopperDraw**: pivot undo actions snapshotted before any move, the component chunk for persistence, and overlay painting and hit-test interception for the collapse horizon.
- **HopperWire**: segment and rectangle intersection helpers, usable in the crossing metric.
- **hoppercode**: the agent's layout rules use 40 px vertically and 50 px between zones. HopperFormat defaults to tighter gaps inside a function. The gap inputs let the two be aligned if that turns out to matter.

## 3. What the user sees

### The FF gesture

Tap F twice with the canvas focused. Same hook, timing window, and feedback pill as GG and DD. **The target is exactly the selection.** FF never resolves to an enclosing group, never uses the group under the cursor, and never formats the whole document.

- Selected components and floating params are laid out as one function (phase one).
- Each selected group has its contents formatted bottom-up; two or more selected units (groups or loose nodes) are then arranged by dataflow (5.9).
- With **nothing selected**, FF formats every group in the document ("format all", below).

The pill names the target after the first tap, for example `Format selection (14 objects) · tap F again`. The result is one undo record: `Format selection`, or `Format groups` when the selection contains groups. Groups tagged `[lock]` are never formatted or moved.

### Format all: FF with nothing selected (phase 2)

With **nothing selected**, FF formats every group in the document (5.9.4): each group's contents are formatted, then the groups are tidied in place. Ungrouped objects never move.

- **Gesture:** the same F double-tap, timing window and pill as FF. The first tap names the target before anything moves, e.g. `Format all groups (12) · tap F again`. With no formattable group, the pill says `No groups to format`.
- **Undo:** one record named `Format all groups`, reverted by a single Undo.
- **Menu:** the component menu offers the same action as `Format all groups`, whatever is selected.
- **Hook change:** the F registration now also captures F when the selection is empty. This replaces the earlier F-then-G idea. That idea needed two-key sequences in the shared hook, and clashed with HopperGroup's G whenever something was selected.

### Option tags (in the group nickname, case-insensitive, like `[lock]`)

| Tag | Effect on FF |
|---|---|
| `[keep-order]` | Keep the user's current vertical order inside each column. Crossing reduction is skipped. |
| `[loose]` | Do not pull nodes whose outputs leave the group into the last column. |

Tags only change how FF formats. They never cause formatting on their own.

### Component

`Params > Util > Hopper Format`. Inputs: `Enabled`, `Gap X` (default 30), `Gap Y` (default 16), `Stack Gap` (default 6, between stacked widgets), `Debug`. Outputs: `Status`, `Moves` (last run), `Log`. One active component per document, like HopperGroup. The component also exposes `Format selection` in its menu for people who prefer a click to a gesture.

## 4. Architecture

```text
FF gesture ──► Target + validation ──► Snapshot (plain data) ──► Solver (pure) ──► Apply
                                                                                   │
                                            one undo record, built before any move ◄┘
```

- **Snapshot** turns live document objects into plain records (section 5.1). Nothing after this touches Grasshopper until Apply.
- **Solver** is a pure, deterministic function from snapshot to moves, tested headless like HopperGroup's regression runner.
- **Apply** builds the undo actions, writes pivots, expires layouts and group caches, remeasures, and records.

## 5. The formatting algorithm

A Sugiyama-style layered layout specialised for Grasshopper: columns are dataflow ranks, rows are ordered to reduce crossings, coordinates are chosen to make the main wires as horizontal as the separation constraints allow, and widgets stack against their consumer.

### 5.1 Input model

```text
Node
  id            Guid                         instance guid, or a rigid child group's id
  kind          Component | Param | Relay | Widget | Cluster | RigidGroup
  size          (w, h)                       Attributes.Bounds after a forced layout
  pivotOffset   (dx, dy)                     Pivot - Bounds.TopLeft, measured in this snapshot
  inGrips[]     (x, y) per input port         parameter attributes' InputGrip, relative to Bounds.TopLeft
  outGrips[]    (x, y) per output port        parameter attributes' OutputGrip, relative to Bounds.TopLeft
  currentTop    (x, y)                       Bounds.TopLeft now, for tie-breaks and keep-order

Edge
  from, fromPort, to, toPort                 one edge per wire, never merged

External
  inbound[]   (node, port, sourceGripY)      wires arriving from outside the target
  outbound[]  (node, port, targetGripY)      wires leaving the target

Anchor   top-left of the content envelope: the union of member bounds before formatting
Gaps     gapX, gapY, stackGap
Options  keepOrder, loose
```

Widget means a parameter with no sources, no owner, and compact attributes: slider, toggle, button, value list, colour swatch, panel used as an input. A panel that has sources is a sink, not a widget. Relays stay as nodes. Clusters are opaque nodes with their outer grips. Grip coordinates are measured, not derived from bounds, because custom attributes do not put grips where right-aligned bounds would.

### 5.2 Step 0: collect and validate

1. Members are the target's direct objects. A nested group inside the target is one `RigidGroup` node sized by its current bounds. Its members are not nodes. It is moved as one body.
2. **Ownership check.** If any object in the target also belongs to a group outside the target's tree, formatting stops with a diagnostic naming the object. Overlapping membership has no single correct layout.
3. On the UI thread, create missing attributes and force a layout pass on every member so bounds and grips are valid. Objects whose bounds are still empty are reported and excluded. Nothing else is skipped silently.
4. For every input parameter of every member, walk `Sources`. Source owned by another member: internal edge. Otherwise an external inbound record with the source grip's y.
5. For every output parameter, walk `Recipients`. Recipient outside: external outbound record with the target grip's y.
6. Hidden wires are still edges. Wire display state never changes topology.

### 5.3 Step 1: cycle check

Depth-first search over all nodes, not only sources. Any back edge stops formatting with a diagnostic naming the two components. Grasshopper refuses direct cycles, so this fires only on unusual documents, and refusing is better than guessing.

### 5.4 Step 2: ranking (which column)

Three passes, in this order.

**ASAP (longest path from sources).**

```text
rank(v) = 0                       if v has no internal predecessors
rank(v) = 1 + max rank(u)         over internal predecessors u
```

**Return constraint, before any pull.** Unless `loose`: every node with no internal successors and at least one outbound wire is fixed at its island's `maxRank`. Output params also pin there: `NodeKind.Param` with at least one internal predecessor and no internal successors, even without an external outbound wire. Panels are excluded from this param rule and stay next to what they display. A node with both internal and external consumers keeps its ASAP rank. Fixing returns first means the next pass can pull feeders toward them.

**ALAP pull.** In reverse topological order, for every unpinned node with internal successors and no outbound wires, except input params when the return constraint is enabled. Input params are `NodeKind.Param` with no internal predecessors, at least one external inbound wire, and at least one internal successor; they retain ASAP column 0. Panels and components do not get this exemption.

```text
rank(v) = min rank(s) - 1         over internal successors s
```

This is what puts a slider beside the component it feeds instead of in column 0 with a long wire. Source-free widget params (including internalised data, toggles, value lists and buttons) still take this pull. `Options.ReturnConstraint = false` (`loose`) disables the input-param exemption and both the output-param and outbound-wire terminal pins together.

**Orphans.** A node with no edges keeps its relative position: it takes the rank of the ranked node whose current x is nearest to its own. Disconnected islands are ranked independently and stacked below the main graph in rank order.

### 5.5 Step 3: column geometry

```text
colWidth[c]   = max size.w over nodes in column c
colLeft[0]    = anchor.x
colLeft[c]    = colLeft[c-1] + colWidth[c-1] + gapX
node.left     = colLeft[c] + colWidth[c] - size.w          (right-aligned bounds)
```

Right alignment puts output grips on roughly one vertical line per column so wires to the next column start together. It is a heuristic, not a guarantee, because custom attributes place grips where they like.

### 5.6 Step 4: vertical order (crossing reduction)

Skipped when `keepOrder`: the order is `currentTop.y` ascending.

**Edges stay port-level.** Two wires between the same pair of components can cross each other if their ports are in opposite order. Merging them would hide exactly the crossings the user sees.

**Long edges** that span more than one column get a virtual node in each intermediate column for ordering and lane clearance. Without them the crossing count ignores most fan-out wiring.

**Initial order.** Column 0 is sorted by the y of its external inbound wires where present, then by `currentTop.y`. Other columns start from `currentTop.y`. The last column is sorted by the y of its external outbound wires where present. These hints keep the group's boundary wiring from getting worse while its inside gets better.

**Sweeps.** The measure for a node is the mean of its neighbours' grip positions, not their row indices.

```text
left-to-right, for node v in column c:
    bary(v) = mean over edges (u → v) of  outGrip(u, fromPort).y - inGrips[v][toPort].y
right-to-left uses successors and  inGrip(s, toPort).y - outGrips[v][fromPort].y
```

Nodes with no neighbours in the sweep direction keep their slot. Sort each column by `bary`, ties by `currentTop.y`, then by instance guid. After every sweep count crossings between each adjacent column pair over port endpoints: sort edges by source grip order, count inversions of target grip order. Up to 8 sweeps, stop when the count stops improving, keep the best order seen.

### 5.7 Step 5: vertical coordinates (rigid alignment and wire lanes)

Keep step 4's column order. Real items retain their measured heights and grips; a virtual item has height 0 and grips at y = 0, representing a long wire's lane through that column. Only complete, straight chains reserve space, even when this makes the selection taller.

**Alignment.** Visit adjacent column pairs once, left to right. Each item takes at most one predecessor, and each predecessor at most one successor. Accepted alignments form rigid blocks with:

```text
offset(v) = offset(u) + outGrip(u, p).y - inGrip(v, q).y
```

Process long-edge segments first: virtual items take their unique predecessors, and real targets take incoming virtual items before short wires. If a source starts several long edges, prefer the lowest output port, then the longest span, then the higher target in column order. Next, visit remaining real targets top to bottom and try their median in-edge by input port order (upper median: ports 0, 1, 3 for 2, 3, 8 inputs); if unavailable, try candidates nearest that median, preferring the upper one on ties. Reject an alignment that crosses an accepted alignment in the same pair. Endpoint conflicts or crossing conflicts can leave a wire bent; a fully aligned long edge stays horizontal along its whole lane, and its receiver follows that lane even when a shorter wire bends.

When the chosen predecessor feeds a target through several wires, align on those wires' upper-median input port, with output port as the tie-break. The lane-first pass uses the same representative for parallel long wires sharing a source and target, so the middle wire reserves the lane.

After alignment, remove every incomplete long-edge chain's virtual items from placement and repeat alignment. Keep those items in step 4 for crossing counts. Each unsuccessful pass removes at least one chain, so this terminates deterministically. Grasshopper draws a bent long edge as one bezier; intermediate virtual positions leave no visible trace and must not reserve empty space.

**Separation.** Between adjacent items in a column: real/real uses `gapY`, or `stackGap` when both are widgets whose only internal successor is the same node; real/virtual uses `gapY` for lane clearance; virtual/virtual uses `stackGap`. Independently rounded pivots can shrink clearance by up to one unit; integer geometry keeps the exact gap. A bent edge can happen to have level endpoints without being a reserved lane.

**Top-down compaction.** Each block moves as one body. For every member `w` with an item `a` directly above it, impose:

```text
blockTop(w) >= blockTop(a) + offset(a) + height(a) + sep(a, w) - offset(w)
```

The non-crossing chains make these block dependencies acyclic. Place upper neighbours first, memoising their positions in deterministic order; a block without upper neighbours starts at 0, and every other block takes the maximum of its lower bounds. This longest-path compaction places blocks as high as separation allows without breaking their horizontal alignments.

**Free-source balance.** After compaction, visit blocks bottom-up in reverse deterministic topological order of the above constraints. A free source is a single real node with no internal predecessors and no outgoing alignment link. Its desired top is the upper median of `targetTop + targetInputGrip.y - ownOutputGrip.y` over all outgoing wires, using final target positions. Move it down only, never above its compacted top, and cap its top at the minimum `belowTop - separation - height` over items directly below it, including virtual lanes; no item below means no cap. Lower blocks settle first, so each cap uses their final tops. This centres an unaligned panel on the inputs it feeds while retaining column order and lane clearance. Emit only real nodes. Column widths, island stacking, anchoring and pivot rounding remain unchanged.

### 5.8 Step 6: fixed point, anchor, convert

1. **Geometric fixed point.** Recognize visible structure before optimizing: link adjacent-column wires whose grips are level within one unit; for a long wire, also require `gapY - 1` clearance from every intermediate real node before reconstructing its virtual lane items. Union all links into rigid groups, allowing several links per node to absorb accidental level wires. Order real nodes by top and lanes by wire y, retain measured offsets within groups, and use step 5's top-down compaction, separations and bottom-up free-source balance. Balance only single-node source groups with no visible level outgoing link; a source that reached such a wire is already rigidly linked to its target. Recover near-tight rounded contacts between groups and islands without accumulating rounding error. Accept only integral pivots whose reconstructed positions admit one common translation under the half-open ±0.5 rounding intervals. Alignment priorities are never replayed for recognition. Feed back an unrecognized result for up to 3 rounds; report `unstable` if it still has not settled.
2. Translate each round's placed bounds to that round's union anchor and convert with `pivot = top + pivotOffset`, rounding to whole canvas units.
3. After the last round, shift the whole result by a whole-unit vector that puts its union top-left within 0.5 of the **original** anchor. This removes accumulated anchor drift while preserving integral pivots and the fixed point. Emit a move for every node whose rounded pivot differs from its current one.

### 5.9 Groups (phase 2)

Phase one formats one flat selection. Phase two adds groups at two levels. **Inside** each group, the members are laid out with steps 1–6, as in phase one. **Between** groups, the groups themselves are arranged. A selection and format all (FF with nothing selected) differ only in what they target and in how the groups are arranged (section 3).

#### 5.9.1 Group tree and ownership

- Membership comes from `GH_Group.ObjectIDs`. A group's **direct members** are its nodes, plus its child groups as single units; a child group's own members are not direct members.
- A **label group** contains exactly one node, transitively. Example: `TitleBlockRange` wrapping one param. Label groups are **see-through**: their node counts as a direct member of the parent, and the label box follows it because Grasshopper derives group bounds from members. This keeps every phase-one golden layout unchanged when its params are labelled.
- **Ownership must be exclusive.** A node in two groups that are not nested in each other has no single correct layout. FF on a selection refuses with a diagnostic naming the node; format all skips that top-level group and reports it.
- `[lock]` groups are never formatted and never moved. In format all they stay put as obstacles.

#### 5.9.2 Inside groups (bottom-up)

Groups are processed in post-order: children before parents.

1. Lay out the group's units with steps 1–6, anchored at their current envelope's top-left: direct nodes, see-through label nodes and real child-group boxes. Non-node rectangles do not contribute to this anchor. Non-node members are never formatted: they keep their absolute positions during inner formatting, except that a member entirely below the old units envelope (top ≥ old bottom) follows positive bottom-edge growth, and a member entirely right of it (left ≥ old right) follows positive right-edge growth (both apply at the bottom-right). Note follow-growth is rounded up to whole canvas units. Above, left and overlapping members otherwise stay put; any overlap with formatted nodes is logged as `Note '<label>' overlaps formatted nodes in '<group>'` without failing. All non-node members translate with their containing group when it moves at an outer level. On a second run there is no growth, so they stay put.
2. A child group takes part in its parent's layout as one **RigidGroup** node:
   - Its **size** is the child's formatted member envelope plus the child's padding. The padding is measured per side from its current bounds minus its current member envelope, so it is never assumed. This visible envelope includes child-group boxes, including label boxes, and non-node rectangles; labels still never become layout units. Keeping the visible envelope separate from the see-through layout envelope preserves both measured Grasshopper bounds and phase-one coordinates.
   - It has one **input port** per member receiving a wire from outside the child, at that member's input-grip y relative to the child box top.
   - It has one **output port** per member sending a wire out of the child, at that member's output-grip y.
   - Wires stay port-level, as in phase one.
3. Contract each child to one node and check the contracted graph for cycles. `a1 → b → a2`, with `a1` and `a2` in child A and `b` outside it, contracts to a cycle. It is refused (selection) or skipped (format all) with a diagnostic.
4. Group attributes have no writable pivot. A group moves by moving each of its descendants. All inner moves and outer translations are composed into **one final pivot per descendant** before anything is written, and recorded in one undo record (section 6).

#### 5.9.3 Arranging groups with FF: dataflow re-plan

Used when FF's selection holds two or more top-level units, where a unit is a group or a loose node. This is a deliberate "make these read as a pipeline" action.

- **Graph:** run the same solver one level up. Each selected top-level group is a RigidGroup node (5.9.2); loose selected nodes stay ordinary nodes. Edges are the wires between units, port-level.
- **Gaps:** groups are separated by `GroupGapX = 50` and `GroupGapY = 40` (hoppercode's zone spacing). Loose nodes keep `GapX` and `GapY`.
- **Box-edge alignment:**
  - Between two groups, an alignment link makes their **tops** equal instead of their grips.
  - A column that holds only groups is **left-aligned**.
  - Any link or column involving a loose node keeps the phase-one rules: grip alignment and right-aligned columns.
- **No lanes at group boundaries:** long wires with a RigidGroup at either end get no reserved lanes. Grasshopper draws wires above group boxes, so they read as crossing over a group, not plugged into it. Virtual nodes are still used for crossing reduction.
- **Anchor:** the selection's top-left. Nothing outside the selection moves.
- A selection that is exactly one group formats that group's contents only.

#### 5.9.4 Arranging groups with format all: tidy in place

Format all (FF with nothing selected) targets every top-level group in the document. It keeps each group roughly where the user put it.

1. Format every target group's contents (5.9.2), each anchored at its own top-left.
2. **Rows.** Sort groups by top. A group joins the current row when its top is within `RowSnap` of the row's top; `RowSnap = min(GroupGapY, 0.25 × the shorter height)`. Every group in a row takes the row's smallest top, so box edges line up.
3. **Columns.** Across rows, left edges within `GroupGapX / 2` of each other snap to their smallest value.
4. **Gaps.**
   - Within a row, in left-to-right order, a gap smaller than `2 × GroupGapX` becomes exactly `GroupGapX`, pushing later groups right. Larger gaps are intentional separation and are kept.
   - Rows are handled the same way top to bottom with `GroupGapY`; a row moves as a whole.
5. **Obstacles.** Ungrouped objects, `[lock]` groups and skipped groups **never move**; they are fixed rectangles. If a group would overlap one, push it right within its row (or push its row down) until it clears by the group gap.

#### 5.9.5 Guarantees for groups

- **Idempotent:** FF on a selection or format all on a freshly formatted result moves nothing, at both levels. The geometric fixed-point recognition of 5.8 extends to RigidGroup nodes and to the tidy pass. Snapped edges and gaps of exactly `GroupGapX`/`GroupGapY` are stable.
- **Deterministic and non-overlapping:** among group boxes, and between groups and obstacles.
- **Non-targets untouched:** FF on a selection moves only selected units and their descendants; format all moves only members of target groups.
- **Phase-one layouts unchanged:** a flat selection, or a group whose only nested groups are label groups, lays out exactly as in phase one.

### 5.10 Guarantees the tests enforce

All measured on the applied, remeasured layout, not on the solver's internal numbers.

- **Idempotent**: FF on a freshly formatted target emits zero moves.
- **Deterministic**: the same snapshot gives the same moves in the same order.
- **No overlaps** among the target's own nodes.
- **Left-to-right dataflow**: for every internal edge, `left(from) < left(to)`.
- **Anchored**: the target's content envelope keeps its top-left.
- **Non-members untouched**: the move list contains only members.

Crossings are a reported metric, logged before and after, not a guarantee. The before and after layouts have different columns and the counts are not strictly comparable.

### 5.11 Worked example

A group with: slider A, slider B, `Construct Point` P consuming A and B, `Circle` C consuming P and a slider R, `Boundary Surfaces` S consuming C, and S's output wired to a Custom Preview outside the group.

- Ranking: ASAP gives A, B, R rank 0, P rank 1, C rank 2, S rank 3. The return constraint fixes S at 3. The ALAP pull moves R to rank 1 beside C.
- Columns: `[A, B]`, `[P, R]`, `[C]`, `[S]`, each right-aligned.
- Order: A above B because A feeds P's first input. R below P because C's radius input is below its plane input.
- Coordinates: P's output grip meets C's plane input and C's output meets S exactly, because each is a single node with room to move. A and B stack with `stackGap`; P aligns its first input to A (the upper median of two), while B's wire may bend. For eight stacked inputs the component instead aligns to input port 3.
- Result: a four-column function with the main chain horizontal, sliders pressed against their consumers, box shrunk around it.

## 6. Running FF

Sequence on the second tap:

1. Resolve the target (section 3). Refuse `[lock]`.
2. Validate: ownership, cycles, valid bounds. On failure, show the diagnostic in the pill and log, move nothing, record nothing.
3. Snapshot on the UI thread, then solve.
4. **Build one pivot undo action per moved member before writing any pivot.** Pivot actions capture the pivot at construction, so building them afterwards would record the formatted state.
5. Write pivots, call `ExpireLayout` on each moved object, then `ExpireCaches` on every affected group so group bounds refresh.
6. Remeasure. If any member's applied pivot is not what the solver emitted, revert every pivot and record nothing.
7. Add the actions to the one record named `Format selection` or `Format groups`, refresh the canvas, and update the pill (for example, `Formatted groups · 11 moves`). The HopperGroup handshake is a later stage, described below.

**HopperGroup handshake.** HopperGroup keeps settled positions and exit outlines from the last drop, and treats any other movement as an external move to reconcile on the next drag. That reconciliation could read a formatted member as having left its group.

The fix lives in HopperGroup and is small. When an undo record whose name starts with `Format ` is added (`Format selection`, `Format groups`, `Format all groups`), HopperGroup re-snapshots settled positions and outlines for the affected groups, exactly as its own Refresh does.

Format all moves nearly every group, so this handshake ships before format all is recommended alongside HopperGroup (phase 2, stage 3). Until then, the HopperFormat README tells users to tap Refresh on HopperGroup after formatting.

No background triggers exist in phase one. There is nothing to debounce, no re-entrancy to guard, and no drag to stay out of.

## 7. Coexistence

- **HopperGroup**: see the handshake above. F is free in its hook, but the hook's message handler is G-specific and must be generalised to a registered key rather than copied.
- **HopperWire**: tighter groups make its length and boundary rules more meaningful. No change.
- **HopperDraw**: annotations use absolute coordinates and are not members, so they do not move. Its pattern of snapshotting undo actions at press time is the one HopperFormat follows.
- **hoppercode**: the agent can call the same solver in-process through a `gh_format` tool once phase one exists, replacing its size-table placement for groups it creates.

## 8. Testing

Headless console runner, same shape as HopperGroup's:

- Property tests over random DAGs for every guarantee in 5.10, including the snapshot, apply, remeasure, solve-again loop.
- Golden tests: the worked example, a diamond, a fan-in of eight sliders, a long chain, a tall multi-input component, an orphan panel, an island.
- Validation tests: overlapping membership refused, cycle refused, empty bounds reported.
- Undo tests on a simulated document: one record, Undo restores every pivot, Redo reapplies, nothing recorded on failure.
- Windows input tests for the FF gesture, adapted from HopperGroup's.

Live Rhino checklist before release: FF with HopperGroup installed, then a drag of a formatted member, then Undo. Auto-pan, clusters, scribbles, custom attributes, high-DPI bounds.

## 9. Phases

1. **FF on a flat target.** Pure solver, headless tests, one-shot format with its own undo record, external hints. Shipped as the selection-only MVP (PR #1); option tags deferred.
2. **Groups** (5.9), in three stages:
   1. **FF with groups.** Groups in the selection: exclusive-ownership check, see-through label groups, bottom-up inner formatting with RigidGroup children and composed pivots, and the dataflow re-plan with box-edge alignment for several units (5.9.3).
   2. **Format all.** FF with nothing selected and the `Format all groups` menu item, plus tidy-in-place arrangement with fixed obstacles (5.9.4).
   3. **HopperGroup handshake.** The record-name convention in HopperGroup (section 6), shipped as a HopperGroup release before format all is recommended alongside it.
   
   Later: the `gh_format` tool for hoppercode.
3. **Optional continuous mode**, only if phase one proves the solver calm. An explicit `[flow]` tag re-runs FF after a drop inside that group, built on the handshake. `[pin]` is deferred: Grasshopper moves only the dragged object during a drag, so a rigid group needs interaction-level interception like HopperDraw's, not a re-translate on drop.
4. **Horizon.** Collapse to a function box.

## 10. Risks

- **Formatting a target the user did not mean.** Selection-first resolution and naming the target in the pill before the second tap address this. Undo covers the rest.
- **Solver output that looks worse than the hand layout.** Port-level edges, external hints, and the crossing log make this measurable. `[keep-order]` is the escape hatch.
- **Odd attributes.** Clusters, scribbles, and custom attributes with grips away from their bounds are handled by measuring grips rather than deriving them. Empty bounds are reported, never guessed.
- **Shared code**: copy the shortcut hook and session registry from HopperGroup rather than extracting a library yet.

## 11. Horizon: collapse to a function box

Once a group is formatted, it can be drawn as a single box with its external inputs on the left edge and outputs on the right, without making it a cluster. Clusters are a separate document and painful to edit live; this is a view. HopperDraw proves overlay painting above members and hit-test interception through the HWND hook. What it does not prove is native wire rendering, selection, and editing against hidden members, so collapse is its own design, not a phase of this one.

## 12. Open questions

Resolved:
- **Gaps:** 30/16/6 inside groups; `GroupGapX = 50` and `GroupGapY = 40` between groups.
- **Return constraint:** on by default.
- **Selection spanning groups:** FF formats each group's contents, then re-plans the groups by dataflow (5.9.3).
- **Global gesture:** FF with nothing selected formats all groups (F then G was dropped: it clashed with HopperGroup's G).
- **Format-all arrangement:** tidy in place with box-edge alignment; ungrouped objects never move (5.9.4).
- **Label groups:** see-through.

Open:
- Should `GroupGapX`/`GroupGapY` become component inputs, or stay constants until someone asks?
- Should format all eventually offer the dataflow re-plan too, for example through a `[flow]` tag on a parent group?
