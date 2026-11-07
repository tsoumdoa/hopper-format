# HopperFormat: format a group on demand, read it as a function

Design proposal, revised 2026-11-07 after review. Author: tomoS (aectooling). Status: idea, not started.

## 1. Thesis

A Grasshopper group should read like one small function: all the logic for one job, packed tight, inputs on the left, result on the right. Today that layout is hand-made and decays with every edit.

HopperFormat is a formatter you run, not a rule that runs itself. Tap **F twice** and the selected or hovered group is re-laid out as a dataflow: columns by rank, rows chosen to untangle wires, widgets pressed against their consumers, one undo record. Nothing moves unless you ask.

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

Tap F twice with the canvas focused. Same hook, timing window, and feedback pill as GG and DD. Target resolution, selection first:

1. If there is a selection, the innermost group that contains every selected object. If the selection is not inside one group, the selection itself is formatted as an anonymous group.
2. Otherwise the innermost group under the cursor.
3. Otherwise nothing happens and the pill says so.

The pill names the target before the second tap lands, for example `Format 'Inputs' (14 objects)`. The result is one undo record named `Format 'Inputs'`. Groups tagged `[lock]` are never formatted.

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

### 5.9 Nested formatting (later phase)

Phase one formats one flat target. When nesting comes:

- Children are solved first, bottom-up, and only when ownership is exclusive all the way down.
- The contracted graph (each child as one node) is checked for cycles. `a1 → b → a2` with `a1, a2` in child A and `b` in child B contracts to a cycle and is refused with a diagnostic.
- Group attributes have no writable pivot. A child moves by moving each of its descendants once. All child-internal moves and parent translations are composed into one final pivot per descendant before anything is written.

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
7. Add the actions to a new record named `Format 'Name'`, raise the HopperGroup handshake, refresh the canvas, update the pill: `Formatted 'Inputs' · 11 moves`.

**HopperGroup handshake.** HopperGroup keeps settled positions and exit outlines from the last drop and treats any other movement as an external move to reconcile on the next drag. That reconciliation could read a formatted member as having left its group. The fix lives in HopperGroup and is small: when an undo record named `Format '…'` is added, it re-snapshots settled positions and outlines for the affected groups exactly as its own Refresh does. Until that change ships, the HopperFormat README tells users to tap Refresh on HopperGroup after formatting a group that has nested children.

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

1. **FF on a flat target.** Pure solver, headless tests, one-shot format with its own undo record, external hints, option tags. The solver is roughly the size of the HopperWire refactor; the gesture and apply path are mostly borrowed.
2. **HopperGroup handshake and nesting.** The record-name convention in HopperGroup, exclusive-ownership check, bottom-up nested formatting with flattened transforms, the `gh_format` tool for hoppercode.
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

- Gap defaults: 16 inside a function and 40 only between sibling groups, or hoppercode's 40 everywhere?
- Should FF on a selection that spans several groups refuse, or format the selection as one anonymous group as proposed?
- Should the return constraint be on by default, or should `[loose]` be the default and `[return]` the opt-in?
