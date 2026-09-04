---
name: vrse-interactables
description: >-
  Convert plain art-scene meshes into VRseBuilder interactables — grabbable, touchable, and
  placepoint objects, plus spawn/teleport points — and harvest their IDs for use in a Story.
  Use whenever a user wants to "make this object grabbable/touchable", "add a place point",
  "set up a spawn/teleport point", or "get the object IDs for the story" before or while
  writing story actions/triggers. Explains the batch setup-objects/place-objects pipeline
  (preferred) vs. the single-object interactable-convert fallback, and the harvest step a
  Story's node Query fields depend on.
---

# VRse interactables (grabbable / touchable / placepoint / spawn)

A Story's trigger and action nodes reference **objects in the art scene** — a `HandTouchTrigger`
needs a touchable, an `onRight` grab-check needs a grabbable, a "place the part here" moment
needs a placepoint. Plain art meshes aren't any of these until you convert them. This skill
covers turning art assets into interactables and getting their IDs so story nodes can
reference them.

## The three (plus one) interactable types

- **grabbable** — can be picked up/held. Converts via
  `MetaXRInteractableConverter.ConvertToNetworkMetaXRGrabbable`.
- **touchable** — responds to a hand/controller touch (drives `HandTouchTrigger`). Converts
  via `ConvertToTouchable`.
- **placepoint** — a target socket that accepts a specific grabbable being placed on/near it
  (drives placement checks in `onRight`). Two flavors:
  - **marker-based** — a fixed spot, sized from an `allows`-listed grabbable's bounds.
  - **spatial** — computed from an `on` (surface) or `inside` (container) reference instead of
    a fixed marker, via `vrse/place-objects`.
- **spawnpoint / teleport** — not a "thing the learner interacts with" but a pose the runtime
  spawns/teleports the learner (or an object) to; also handled by `vrse/place-objects`, keyed
  by `near`/`marker` instead of `on`/`inside`.

## Preferred pipeline: batch setup + place (Level 4/5)

Use these for anything beyond a one-off single object — they're the ported,
parity-with-hand-tooling batch path, and they auto-add the `GameObjectQuery` component every
harvestable object needs.

1. **`vrse/setup-objects`** — pass `objects: [...]`, one entry per object:
   `{ type: "grabbable" | "touchable" | "placepoint" | "simple", source, name, parent, anchor?, preferScene? }`.
   - `source` is the existing art-scene object to duplicate/convert; `name` is the logical
     name the story will reference; `parent` is the destination container path (created
     lazily if missing).
   - `type: "placepoint"` here is the **marker-based** flavor — pass `allows` (CSV of grabbable
     name(s)) so the trigger collider is sized correctly from that grabbable's bounds.
   - `type: "simple"`/`"duplicate"` just duplicates without converting — use for plain
     decoration objects that still need a stable name/GOQ.
   - Each result reports `ok`/`skipped`/`outcome` per object — check these; a `skipped` object
     (already converted) is not a failure.
2. **`vrse/place-objects`** — pass `objects: [...]` for the **spatial** cases setup-objects
   doesn't cover: a placepoint keyed by `on`/`inside` (computes pose from a surface/container
   raycast instead of a fixed marker), or a spawnpoint/teleport keyed by `near`. Still needs
   `allows` for placepoints, for the same collider-sizing reason.
3. **`vrse/harvest-ids`** — run **after** setup/place, before writing story nodes that
   reference these objects. Enumerates every live `GameObjectQuery` in the loaded scenes
   (including ones just added) and returns the `name → id` map. Story node `Query`/object
   references need real IDs from this map — until harvested, they carry placeholder IDs
   (`Name#$0`), which is expected mid-build but must be resolved before the story is final.
   Optional `save` persists scenes first; `root` scopes the harvest to a subtree (default
   `QueryObjects`) — pass empty to harvest everything loaded.

## Fallback: single-object conversion

**`vrse/interactable_convert`** — converts **one already-placed** object in place, by
`methodName` (`ConvertToGrabbable`, `ConvertToTouchObject`, `ConvertToPlacePoint`,
`CreatePlacePoint`, `ConvertToRayInteractable`, `ConvertToVRseObject`) and `targetHint`/
`objectPath`. Use this only for a quick one-off tweak to an object that's already in the
scene and doesn't need duplicating/parenting/sizing — for anything with multiple objects or
that needs a placepoint's collider sized from a grabbable, use `setup-objects`/`place-objects`
instead, which do more of that work for you.

## Shortcuts: prebuilt building blocks

**`vrse/building-blocks-list`** then **`vrse/building-blocks-instantiate`** (`blockName`) drop
in a ready-made prefab (with its `actionName`/`triggerName` already wired) from the project's
`VRseBlocksCollection` instead of hand-converting a mesh. Check the list first — if the thing
the user wants (a button, a lever, a common fixture) already exists as a block, instantiating
it is faster and less error-prone than `setup-objects` + manual story wiring.

## Related single-mesh conversions

- **`vrse/create-button-from-mesh/analyze`** → **`.../create`** and
  **`vrse/create-rotator-from-mesh/analyze`** → **`.../create`** — two-step (analyze, then
  create) converters that turn a plain mesh into a physical button or a bounded rotator/dial,
  for objects a plain grabbable/touchable doesn't fit. Always run `analyze` first and check
  its output before `create` — it validates the mesh is a reasonable candidate.
- **`vrse/scene-hierarchy-checkup`** — run after a batch of conversions to catch structural
  issues (missing GOQs, orphaned objects) before moving on to story wiring.

## Pitfalls

- **`vrse/query-objects-list` can lie — it reads a stale registry.** It enumerates the scene's
  `QueryObjectsIdManager` registry, which is **not** refreshed for objects `setup-objects`/
  `place-objects` just added until a rebuild/save. Use `vrse/harvest-ids` (which enumerates
  live `GameObjectQuery` components directly) to see or confirm anything created in this
  session — don't use `query-objects-list` right after a setup/place call and conclude an
  object is missing just because it's not listed yet.
- **Forgetting to harvest before writing story nodes.** A node's object reference resolves by
  name at harvest time — if you write `vrse/story-update-node` with a `query` before the
  target exists/was harvested, you'll get a placeholder ID silently. Order matters:
  setup/place → harvest → story node update.
- **Placepoint without `allows`.** Both marker-based and spatial placepoints size their
  trigger collider from an allowed grabbable's bounds — omitting `allows` either fails
  (`vrse/place-objects` requires it for spatial) or leaves the collider unsized.
- **Re-running setup-objects on an already-converted object.** It's idempotent and reports
  `outcome: "skipped"` rather than erroring — don't treat a `skipped` result as a failure, but
  don't assume it re-applied changed parameters either (it didn't touch the existing object).
- **Container spawn objects with no target.** An `Objects` action with `Option: "Spawn"` and
  an empty `Query` renders pink placeholder meshes — only spawn objects that actually resolve
  to something.

Once objects are converted and IDs harvested, use **`vrse-story-editing`** to wire the actual
trigger/action nodes that reference them, or **`vrse-project-module-experience`** if the
project/module/experience isn't set up yet.
