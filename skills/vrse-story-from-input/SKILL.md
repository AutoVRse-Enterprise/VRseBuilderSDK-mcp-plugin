---
name: vrse-story-from-input
description: >-
  Turn ANY input — an SOP, a PDF, a Word/Google doc, or a plain text prompt — into a
  VRseBuilder story JSON, organized as Chapters and Moments. Use whenever a user wants to
  "make a VR training / walkthrough", "convert this SOP/PDF/doc to a story", "build a story
  from this procedure", or "generate story JSON". Enforces a hard approval gate: it first
  ingests the input, drafts a Chapter/Moment outline, presents that outline, and WAITS for
  explicit user approval before generating any JSON. Runs the vrse_parse_storyboard →
  vrse_translate_storyboard → vrse_generate_story pipeline. Stops at validated JSON;
  applying to Unity, harvesting real IDs, and generating VO are documented as optional
  follow-ups.
---

# VRse story from any input

Convert an SOP / PDF / doc / prompt into a VRseBuilder **story JSON** (Chapters → Moments →
typed actions), using the correct pipeline tools. The deliverable is the **approved,
validated story JSON**.

## The one hard rule — approval gate

**NEVER call `vrse_generate_story` (or write final JSON) until the user has seen the
Chapter/Moment outline and explicitly approved it.** Ingest → draft outline → present →
**wait for "approved"** → then generate. If the user changes the outline, re-present and
wait again. This is the single most important behavior of this skill; the raw pipeline will
happily skip it, so you must hold the gate yourself.

## Workflow

### Phase 1 — Ingest the input

Identify what the user gave you and get it into clean text.

| Input type | How to get the text |
|---|---|
| Plain prompt / pasted procedure | Use it directly. |
| `.md` / `.txt` | Read the file. |
| **PDF** | Extract text first. Try `pdftotext "<path>" -` (ships with Git on Windows: `C:\Program Files\Git\mingw64\bin\pdftotext.exe`). Fallbacks: `pypdf`/`python`, or `mutool draw -F txt`. Compressed PDFs return binary if you `cat` them — always extract, never paste raw bytes. |
| `.docx` / Google Doc | Ask the user to export to PDF or paste the text; extract as above. |

If a file path is on the user's machine and you can't read it (macOS blocks `~/Downloads`),
ask them to copy it into the repo or paste the contents.

Once you have text, note anything the story generator will need that the document can't
supply on its own — module name, project name, art scene, dev scene, and the target story
JSON output path. Ask the user for any you don't have; don't invent scene paths.

Also ask, up front, **whether the art/scene is ready** — i.e. the art scene exists at that
path and contains the objects the story will reference. This flow does **not** require ready
art: if the art isn't done or the paths are unknown, that's fine — the skill still produces
the JSON (with placeholder IDs), and real scene objects get wired later in Phase 5. You're
just setting expectations, and capturing the paths for the optional preflight in Phase 4.

### Phase 2 — Parse into a Chapter / Moment structure

Run **`vrse_parse_storyboard`** on the input to produce the structured story. It accepts a
raw SOP or an already-formatted storyboard and returns `{ ok, mode, via, storyboard?, spec,
gaps, stats }` — SOPs are converted to a storyboard via the embedded parse-sop-to-storyboard
rules, formatted storyboards are parsed deterministically. This is the structuring engine;
let it do the first pass rather than hand-authoring the outline.

The result is organized as **Chapters** (phases of the experience), each containing ordered
**Moments**. A moment is **one interaction** — whatever the story calls for: a hand touch, a
grab, a gaze/look-at, proximity, a UI press, a timed beat, and so on. Don't assume any single
interaction style; this skill builds **any** VRse story, not only "touch-a-button"
walkthroughs. Each moment names its own interaction and the object(s) it acts on.

Pass `module` / `project` / `artScene` / `devScene` / `storyJson` to `vrse_parse_storyboard`
when you have them, so the storyboard frontmatter is populated for the later stages.

### Phase 3 — Present the parse output and WAIT (the gate)

Show the user **`vrse_parse_storyboard`'s output** — this is the artifact they approve and
edit, and getting it right here is the whole point of the gate. Present:

- The **storyboard** (the `storyboard` markdown when returned) and/or a readable
  Chapter → Moment summary derived from `spec`, so the user sees each chapter, its moments,
  and the interaction + objects per moment.
- Every entry in **`gaps[]`** — steps with no clear object, missing `[source:]` annotations,
  unrecognized lines — plus any assumptions the parse or you made.

The user reviews and edits at this level: they can rename/reorder chapters and moments, change
interactions, fix objects, or fill gaps. Apply their changes to the storyboard/spec and
**re-present** — repeat until they approve. **Do not proceed to Phase 4 on a soft "looks
good"** — wait for a clear go-ahead. The edited-and-approved storyboard/spec is what feeds the
next stage.

### Phase 4 — Generate the JSON (only after approval)

**Optional scene preflight (non-blocking).** If Unity is connected *and* you have the dev/art
scene paths, run **`vrse_prepare_scene`** first (PATH mode: `devScenePath` + `artScenePath`
from `spec.devScene`/`spec.artScene`; or MODULE mode: `project` + `module`). Pass
`sources: spec.usedSceneSources` so it verifies every referenced object; add
`validateMarkers: true` to also check `<source>_SP`/`<source>_PP` markers. Read
`inventory.found/notFound/duplicates`.

- If everything's found → great, real IDs can be harvested in Phase 5.
- If Unity isn't up, paths are unknown, or some sources are `notFound` → **warn the user which
  objects/paths are missing and proceed anyway.** The JSON will carry placeholder IDs
  (`Name#$0`) that get resolved once the art is built and harvested. **Never let this block
  generating the approved JSON** — it's an advisory check, not a gate.

Then run the pipeline (each MCP tool's own description documents its full contract — read
those at call time; the sequence and intent are below).

1. **`vrse_translate_storyboard`** — turns the approved storyboard/spec into the
   `momentTable` that the generator consumes. Pure compute, no Unity. Check `gaps[]`.
2. **`vrse_generate_story`** with `dryRun: true` — builds and **validates** the story JSON
   and returns it in `produced.storyJson` **without touching any scene**. This is how you
   "get only the JSON". It validates every object reference and reports `missing[]`.
   - If Unity isn't running / you want placeholder IDs, that's fine for a dry run — real IDs
     are wired later via harvest (Phase 5, optional).

Hand the validated JSON to the user. Before you do, run through the **Pitfalls** checklist
below for whatever the story actually uses — e.g. if it has touch triggers, each one has
non-empty `Data`; no empty `Objects`/`Spawn` actions; every highlight/unhighlight balanced.
(The JSON *shape* itself is the generator's own output contract — trust `vrse_generate_story`'s
validation and `missing[]` for that, rather than hand-checking structure.)

### Phase 5 — Optional follow-ups (only if the user asks)

This skill's job ends at approved JSON. If the user then wants it live in Unity:

- **Apply**: `vrse_generate_story` without `dryRun` (applies + saves), or
  `vrse_apply_story_json` for an already-built JSON. Needs a running Unity Editor with a
  StoryCreator. Large JSON (>~70 KB) is too big to pass inline — reload from file via the
  plugin's `SetStoryFromFile()` instead.
- **Real IDs**: `vrse_setup_objects` / `vrse_place_objects` to build scene objects, then
  `vrse_harvest_ids` to get the real `name→id` map, then re-run `vrse_generate_story` with
  that `idMap`.
- **Voice-over**: re-run `vrse_generate_story` with `vo: true`.

## Pitfalls

Non-obvious failures the pipeline tools won't warn you about. Apply whichever match what the
story actually uses, before delivering or applying JSON.

- **Touch never registers → empty `HandTouchTrigger.Data`.** If a moment uses a
  `HandTouchTrigger`, `Data:""` leaves it inert — the trigger never fires and the moment can't
  complete. Populate it, e.g. `Data:"{\"handOption\":\"Any\",\"targetRoleSetId\":0}"`.
- **Pink / broken placeholder meshes → empty `Objects`/`Spawn`.** An `Objects` action with
  `Option:"Spawn"` and an empty `Query` (a container spawn resolving to nothing) renders as
  pink placeholders. Only spawn objects that actually exist; drop no-target container spawns.
- **Object doesn't glow → missing `Highlighter`.** If a moment highlights an object,
  `MetaLayerAction/Edit` needs `"Highlighter":{"setActive":true}` alongside `Outline` and
  `Label`, or it won't glow. On un-highlight, turn all three off together
  (`{"Outline":false,"Highlighter":false,"Label":false}`) so highlight state doesn't leak into
  the next moment.
- **Placeholder IDs are expected on a dry run.** Generating without a harvested `idMap` yields
  `ID:0` / `Name#$0` references and can still be `ok:true` with `missing:[]`. That's fine for
  delivering JSON — real IDs only exist after scene objects are built and `vrse_harvest_ids`
  runs (Phase 5), not a bug.
- **Some plugin versions lack endpoints.** `vrse/harvest-ids` or a VO-harvest route may be
  absent on older plugins ("endpoint not available on this plugin version"). Fallbacks:
  harvest IDs directly via Unity reflection, and reload a fixed JSON from disk with
  `SetStoryFromFile()` in the `VRseBuilder.Core.Framework` namespace.
- **Large JSON can't be applied inline.** `vrse_apply_story_json` chokes on very large
  payloads (~70 KB+). Write the JSON to its file and have the plugin reload it from disk
  (`SetStoryFromFile()`) instead of passing the whole string.
- **PDF extraction gotchas.** `cat`-ing / string-reading a PDF returns compressed binary —
  always extract text. `pdftotext "<path>" -` works and ships with Git for Windows
  (`C:\Program Files\Git\mingw64\bin\pdftotext.exe`; page range via `-f`/`-l`). On Windows,
  `python`/`python3`/`py` are often Microsoft-Store stubs that error — prefer `pdftotext`,
  with `mutool draw -F txt` as a fallback. macOS blocks reading `~/Downloads` (`EPERM`) — ask
  the user to copy the file into the repo or paste its contents.
- **Keep VoiceOver concise.** Dense multi-sentence narration per action reads as tedious in
  VR; tighten each `VoiceOver` to what the moment needs — often 1–2 sentences.

For the story JSON *shape* and each tool's parameters, rely on the live sources rather than a
copy: `vrse_generate_story`'s validated output (`produced.storyJson`, `missing[]`) defines the
schema, and each `vrse_*` MCP tool's own description defines its contract.
