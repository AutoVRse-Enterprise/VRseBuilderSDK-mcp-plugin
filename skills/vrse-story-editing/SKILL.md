---
name: vrse-story-editing
description: >-
  Directly read, build, or edit a VRseBuilder Story (Chapters → Moments → sections →
  trigger/action nodes) in an already-open dev scene, using the vrse/story-* MCP tools. Use
  whenever a user wants to "add a chapter/moment", "add an action to onStart/onAwake/onEnd",
  "wire an onWrong/onRight trigger", "read the current story", "list available node
  templates", or otherwise edit a story node-by-node in Unity — as opposed to generating a
  whole story from an external document (see the vrse-story-from-input skill for that).
  Explains the Story/Chapter/Moment/section data model and the correct
  read-template-first → structure → populate → validate → save tool order.
---

# VRse Story editing (direct, in-Unity)

Use this skill when the user wants to work **directly on a Story already loaded in a Unity
`StoryCreator`** — adding a chapter, wiring a trigger, tweaking one node — rather than
generating a whole story from an SOP/PDF/prompt (that's `vrse-story-from-input`). Both skills
edit the same JSON shape; this one is for surgical, tool-by-tool edits.

## The data model

Every Experience has one **Story**. A Story has ordered **Chapters**; each Chapter has
ordered **Moments**. A Moment is one beat of the experience and has up to seven fixed
**sections**, each a set of nodes:

| Section | Fires when | Node shape |
|---|---|---|
| `onAwake` | Moment is created/loaded | plain **action** list |
| `onStart` | Moment becomes the active moment | plain **action** list |
| `onFirstWarning` | Learner is idle / about to get it wrong, first nudge | plain **action** list |
| `onLastWarning` | Final nudge before failure | plain **action** list |
| `onEnd` | Moment completes (right or wrong) | plain **action** list |
| `onWrong` | An incorrect interaction happens | **array of trigger+action sets** — each entry pairs one trigger node with its own action list |
| `onRight` | The correct interaction happens | **array of trigger+action sets**, plus a `mode` (`InOrder` or unordered) governing how multiple right-triggers must be satisfied |

`onAwake`/`onStart`/`onFirstWarning`/`onLastWarning`/`onEnd` are **simple action sections** —
you add action nodes straight into them. `onWrong`/`onRight` are **trigger-gated** — you must
create a trigger set first (`vrse/story-add-trigger-set`), then add actions *into* that
specific set (`vrse/story-add-action` with `triggerSetIndex`). Section names are
case/separator-insensitive (`"on-wrong"`, `"onWrong"`, `"ON_WRONG"` all normalize the same
way) but always use the canonical camelCase form above when reading responses back.

Trigger and action **node kinds** (`HandTouchTrigger`, `GazeTrigger`, `Objects`,
`MetaLayerAction`, etc.) and their `Option`/`Data` parameters are **data-driven**, not fixed —
they come from the project's `NodeTemplatesData` asset and can vary per project. Never guess
a node kind or its option/data shape; always look it up first (step 1 below).

## Tool order

1. **Look up available node kinds first.** `vrse/story-list-node-templates` dumps every
   trigger/action template with its options and parameters; `vrse/story-search-node-templates`
   with a `query`/`type` (`"action"`/`"trigger"`) narrows it down. Do this before calling
   `story-add-action`/`story-add-trigger-set` so the `nodeKind`, `Option`, and `Data` you set
   afterward are ones that actually exist in this project.
2. **Understand the current story before editing.** `vrse/story-get-info` for a structural
   summary (chapter/moment counts, names); `vrse/story-read` (optionally filtered by
   `section`/chapter/moment) to see actual node contents. Prefer these over assuming state
   from a previous call in the conversation — the story may have changed.
3. **Build structure top-down: chapter → moment → section content.**
   - `vrse/story-add-chapter` (`name`, optional `index`) → note the returned `chapterIndex`.
   - `vrse/story-add-moment` (`chapterIndex`, `name`, optional `index`) → note `momentIndex`.
   - `vrse/story-rename-chapter` / `vrse/story-rename-moment` / `vrse/story-remove-chapter` /
     `vrse/story-remove-moment` for edits, all addressed by index.
4. **Populate sections.**
   - Simple sections (`onAwake`/`onStart`/`onFirstWarning`/`onLastWarning`/`onEnd`):
     `vrse/story-add-action` with `chapterIndex`, `momentIndex`, `section` — creates a default
     node, returns its `nodeIndex`. Then `vrse/story-update-node` to set the real `name`,
     `option`, `query` (object reference), and `data` (JSON string) from the template you
     looked up in step 1.
   - `onWrong`/`onRight`: `vrse/story-add-trigger-set` first (`section`, and for `onRight` an
     optional `mode`) → note `triggerSetIndex`. Then `vrse/story-add-action` with the same
     `section` **and** `triggerSetIndex` to add actions under that trigger. Configure the
     trigger node itself and each action node with `vrse/story-update-node`
     (`nodeKind: "trigger"` vs `"action"`, plus `triggerSetIndex`/`nodeIndex`).
   - `vrse/story-remove-node-by-name`, `vrse/story-remove-action`, `vrse/story-move-action`,
     `vrse/story-duplicate-action` for further node-level edits.
   - `vrse/story-apply-action-to-multiple-moments` to copy one action across many moments at
     once instead of repeating `story-add-action` per moment (e.g. a shared "on fail, play
     buzzer" action across a whole chapter).
5. **Weighting and VO (only if relevant).** `vrse/apply-moment-weightage` sets how moments are
   scored/weighted; `vrse/story-has-pending-vo` + `vrse/story-generate-vo` handle voice-over
   generation for the story's dialogue lines.
6. **Validate before saving.** `vrse/story-validate` — checks the in-memory story for
   structural problems. Fix anything it flags before `vrse/story-save`. **Note:** by default
   it also *mutates* the story — `autoAssignDefaultTargets` defaults to `true` and rewrites
   nodes' default target objects as a side effect of validating. Pass
   `autoAssignDefaultTargets: false` if you need a read-only check (e.g. to validate without
   touching content you haven't finished writing yet).
7. **Save.** `vrse/story-save` persists the in-memory `StoryCreator` story to its JSON file.
   Nothing you do in steps 3-5 is durable until you save.

## Bulk / recovery tools

- **`vrse/story-apply-json`** — replace the whole story from a JSON string in one call
  (what `vrse-story-from-input` uses to apply a generated story). For payloads over ~70 KB,
  write the JSON to disk and reload via the plugin's `SetStoryFromFile()` instead of passing
  the string inline — `vrse/story-apply-json` chokes on very large inline payloads.
- **`vrse/story-patch`** — apply a partial JSON-merge patch instead of replacing everything.
- **`vrse/create-story-backup`**, **`vrse/list-story-backups`**, **`vrse/restore-story-backup`**
  — snapshot/rollback around risky bulk edits. Take a backup before a large `story-patch` or
  `story-apply-json` call you're not fully confident in.
- **`vrse/story-undo-write`** — undo the most recent save-to-disk (distinct from
  `story-defaults-get`, which just reads default parameter values for new nodes).

## Pitfalls

- **Adding an action to `onWrong`/`onRight` without a `triggerSetIndex`.** These two sections
  are arrays of independent trigger+action sets — `story-add-action` needs to know *which*
  set to add into. Create the set first, capture its index, reuse it.
- **Empty `HandTouchTrigger.Data`.** A touch trigger with `Data: ""` never fires. Populate it
  per the template, e.g. `{"handOption":"Any","targetRoleSetId":0}`.
- **Highlight actions missing `Highlighter`.** A `MetaLayerAction/Edit` that sets `Outline`
  and `Label` but not `Highlighter":{"setActive":true}` won't visibly glow. Turn all three off
  together on un-highlight so state doesn't leak into the next moment.
- **Placeholder `Query`/object IDs.** A node's `Query`/object reference will show placeholder
  IDs (`Name#$0`) until the referenced scene object exists and has been harvested — see
  `vrse-interactables` for creating grabbable/touchable/placepoint objects and harvesting real
  IDs. That's expected before the art scene is built, not a bug.
- **Editing without saving.** All `story-add-*`/`story-update-node`/`story-remove-*` calls
  mutate the in-memory `StoryCreator` only — always finish an editing session with
  `vrse/story-save` (after `vrse/story-validate`).

For generating an entire story from an external document instead of editing node-by-node, use
**`vrse-story-from-input`**. For creating the scene objects a story's actions/triggers
reference, use **`vrse-interactables`**.
