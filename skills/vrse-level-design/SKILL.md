---
name: vrse-level-design
description: >-
  Decide what kind of experience is being built and, if it's procedural/compliance training,
  apply VRseBuilder's scene-organization and placement conventions: scene hierarchy, rig
  teleport points, Experience Panel and Checklist UI placement, guidance arrows, warnings, and
  the standard MCQ+report ending. Use whenever a user wants to "set up the scene", "organize
  the level", "where should the rig/panel/checklist go", "make this feel polished/complete",
  or asks for a training/safety/compliance module specifically — and whenever it's unclear
  whether the request is a gated training procedure or something else (exploration, a game, a
  showcase), since that changes which of these conventions apply at all.
---

# VRse level design & scaffolding

Scene-organization and placement conventions reverse-engineered from a cross-section of real
production VRseBuilder modules. Most of what's below is specific to **procedural/compliance
training** — apply it when that's the shape of experience being built, not automatically for
everything. See Step 0.

## Step 0 — decide the experience's shape first

VRseBuilder's primitives (Actions, Triggers, Moments, Chapters) are general-purpose. Checklist
UI, guidance arrows, haptic+voice warnings, and an MCQ+report ending are patterns specific to
**procedural/compliance training** — don't apply them by default just because they're the
best-documented pattern. Before building, work out with the user:

- Is the player being taught a correct procedure (gated, a right/wrong distinction) — or
  exploring/playing freely?
- Does progress need to be gated (must do X before Y), or is free exploration fine?
- Does the player need a persistent status indicator at all — a checklist, a score/objective
  display, or nothing?
- Does it end in an assessment, or just end?

Then match scaffolding to the answer:

- **Teach a correct procedure / compliance / safety** → everything below applies: spawn points,
  Checklist UI, warnings, guidance arrows, MCQ+report ending.
- **Explore / showcase / demo freely** → skip the checklist, warnings, and hard gating; use
  touch/gaze hotspots the player can trigger in any order (see Multi-stage Moments below for a
  "cycle through options" pattern that doesn't require gating).
- **Branching narrative or decision-driven scenario** → use `onRight` holding multiple valid
  paths (see Decisions and branching below), likely no checklist, warnings only where a real
  mistake should force a retry.
- **Game-like / scored / anything else** → reuse the primitives, but don't force a "checklist"
  or "MCQ+report" verbatim if a score/objective display or a custom win/lose Action fits better.
  Ask what the user actually wants rather than defaulting to the training shape.

## Scene hierarchy convention

`QueryObjects/Story Objects` organizes into five top-level folders, and every category holding
per-chapter content splits into a `Common/` folder (chapter-independent) plus one `Chapter N/`
folder per Chapter:

```
QueryObjects/Story Objects/
 ├─ Vrse System/              — rig, Experience Panel, framework systems (not chaptered)
 ├─ Default Query objects/    — CountDownTimer, SFXPlayer, VOPlayer, Haptics (not chaptered)
 ├─ Interactables/{Grabbable, Placepoint, Touchable, Custom Interaction}/{Common?, Chapter N}
 ├─ Non Interactables/
 │   ├─ Props, VFX, Animation, Collission Trigger/{Common, Chapter N}
 │   ├─ Animate Transforms/{Common, ...}   — movable-panel/prop marker transforms, see below
 │   ├─ Spawnpoints/{Chapter N}            — rig teleport targets, see below
 │   └─ Arrows/                            — guidance arrows, flat, not chaptered
 └─ UI/{Checklist UI/{Common, Chapter N}, MCQ UI, Other UI/{Chapter N}, EventSystem}
```

New objects for Chapter 3 go in that category's `Chapter 3` folder, not loose at the top level —
this is what keeps a module navigable as it grows. For the mechanics of converting/tagging
objects (grabbable/touchable/placepoint, `_PP`/`_SP` naming, component stacks), see
**`vrse-interactables`** — this skill covers *where* and *when*, that one covers *how*.

## Rig placement & moving the player

The player is never left to find their own way. Every Moment starting at a new station opens
with `Player`/`Teleport` in `onAwake` to a pre-placed `<Purpose>_SP` transform (`Transform` +
`GameObjectQuery` only, grouped under `Spawnpoints/Chapter N`) — a chapter commonly has three
or four of these. Always `shouldFade: true` with 1–2s `fadeDuration`; a hard cut is jarring in
VR. The module's very first Moment also teleports to the start spawn point in its `onAwake`, so
position is deterministic regardless of Editor camera state.

## Experience Panel placement

**Only at module start**, before Start is pressed, the panel stands directly in front of where
the player will be looking — offset ~0.9m along the rig's forward direction, **same Y-rotation
as the rig** (not opposite). This is a start-of-module behavior, not a rule for the whole
experience.

**Once running**, don't keep re-centering the panel in the player's view — it blocks whatever
they're looking at or working on. Instead, keep it **reachable from wherever the player
currently is**: reposition it to a spot near their current work area (not centered in their
gaze), considering the actual geometry (never inside a wall/machine/prop), moving it again
whenever they move to a meaningfully different area, and never covering the object(s) they need
for the current Moment. Mechanism: pre-place one inactive marker transform per area under
`Non Interactables/Animate Transforms/Common` (nesting a non-functional panel-prefab duplicate
under it helps you eyeball alignment), then move the real panel with:

```json
{"name":"ObjectAnimationAction","option":"PositionRotation",
 "query":"<ExperiencePanelQueryName>",
 "data":"{\"targetTransform\":\"<MarkerName>#$<id>\",\"lerpDuration\":1.0,\"waitForCompletion\":true}"}
```

If the SDK's Watch UI mode is available (a `ChecklistUI` component wired into the panel's own
"Watch UI panel" child hierarchy — check via `unity_component_get_properties`), it's reachable
by construction and may be a better fit than per-area marker authoring.

## Checklist UI

One `ChecklistUI`-component object per module, driven by `ChecklistUIToggle` — never
hand-build it per chapter. Like the panel, it shouldn't be left behind when the player moves
far from where it's parked; reposition it the same way if stations are more than a few meters
apart, or use the Watch UI mode above.

- Chapter's first Moment, `onAwake`: `Configure` with `titleText` + a `toggleInfos` array (the
  chapter's full ordered step list, short imperative phrases). Once per chapter.
- Every Moment's `onAwake`: `Current` with the 1-based step `index`.
- Every Moment's `onEnd`: `Check` with that same `index`.

## Guidance arrows

Pre-place an `IndicatorArrow_<Purpose>` prefab (inactive by default) near each interaction
target, under the flat `Non Interactables/Arrows` folder. Spawn it in `onStart` alongside
highlighting the target (`MetaLayerAction/Edit`, `Highlighter: true`); despawn it the moment
the matching trigger fires. Give a grab-then-place Moment's place-point stage its own matching
`_PP`-suffixed arrow variant.

## Multi-stage Moments & decisions

"Grab X, then place it at Y" is **one Moment**, `onRight.mode: "InOrder"` with two sequential
`triggerActionSets`: Stage 0 (`GrabbableTrigger/Grab` → despawn arrow, spawn the `_PP` place
point which starts inactive, spawn a new arrow) then Stage 1 (`PlacePointTrigger/Place` →
despawn arrow, disable grabbing). This generalizes past two stages: a mode-cycling interaction
(tap to cycle through several states) is the same `InOrder` pattern with three-plus stages, each
one an invisible proxy object representing the current state, chaining forward to the next.

`onWrong` means a genuine mistake that should make the player retry the *same* thing — the
Moment does not complete. When a player is choosing between options where **any** choice should
let the story continue (just with different consequences/feedback), put all of them in
`onRight` with `mode: "Any"`, differentiated by their actions, not split across `onWrong`.
Reserve `onWrong` for real do-overs. For the mechanics of adding trigger sets/actions, see
`vrse-story-editing`.

## Animated props & locking

To relocate an animated prop (e.g. a vehicle) without a visible glide:
`ComponentToggleAction/Disable` (`componentName: "Animator"`) → `ObjectAnimationAction/
PositionRotation` with `lerpDuration: 0` → `ComponentToggleAction/Enable`. To keep a grabbed
object fixed once correctly placed (a seatbelt clip, a latch): `GrabLockAction/GrabLock` right
after the successful grab, `GrabUnlock` (`forceRelease: true`) once placed.

## The standard training-module ending: MCQ block + report

The last Chapter is conventionally named something like "MCQs" and follows this shape:

1. Transition Moment: fade out, despawn Checklist UI, move the panel to a quiz-area marker,
   teleport to a dedicated quiz spot.
2. One Moment per question: `MCQResponseAction` (`questionIndex`, `questionText`,
   `optionsList`, `correctOptionIndex`) in `onStart`, gated only by
   `MCQResponseTrigger/AnyResponse` in `onRight` — **even graded questions use `AnyResponse`**;
   scoring against `correctOptionIndex` happens via the evaluation system in the background,
   not by blocking on a correct answer.
3. Final "Module Completed" Moment: closing `VoiceOver`, panel animated to a final marker, a
   report/summary panel object spawned.

## Warnings & feedback

`onFirstWarning`/`onLastWarning`: a `HapticsAction/Both` pulse (`intensity: 1, duration: 1`)
paired with a `VoiceOver` restating the instruction — haptics alone don't say what to do.
`onEnd` on success plays `StorySFX/CorrectSFX` via `SFXPlayer/Play`; `onWrong` plays
`StorySFX/WrongSFX`. Break narration into several short sequential `VoiceOver` actions per
Moment rather than one long paragraph.

## Pitfalls

- **Place points and arrows start inactive.** A `PlacePointTrigger` with nothing spawning its
  `_PP` object first will never fire — check whether it was actually spawned.
- **Object/component names in this file are placeholders.** Different modules name their panel/
  checklist/spawn objects differently — always resolve the real `GameObjectQuery` names in the
  module you're working on rather than reusing a literal name from here.
- **Don't apply training scaffolding to a non-training request.** If the shape (Step 0) is
  exploratory/showcase/game, adding a checklist and warnings the user didn't ask for is over-
  building, not polish.

For creating/tagging the actual interactable objects referenced above, use
**`vrse-interactables`**. For the mechanics of wiring the trigger/action nodes described here,
use **`vrse-story-editing`** (or **`vrse-story-from-input`** when generating a whole module from
a document/prompt).
