---
name: vrse-project-module-experience
description: >-
  Navigate or create VRseBuilder Projects, Modules, and Experiences (Training / Evaluation)
  via the vrse/* MCP tools. Use whenever a user wants to "open/select a project", "create a
  module", "create a training/evaluation experience", "find the art or dev scene for a
  module", or "set up a new experience" before building a story. Explains what a Project /
  Module / Experience is in VRseBuilder, and the exact vrse/* tool order — status → login (if
  needed) → list projects → select project → list modules → create/open experience — so an
  agent doesn't call these out of order or skip the selected-project prerequisite.
---

# VRse Project / Module / Experience setup

VRseBuilder is AutoVRse's VR framework, used mostly to build **VR training applications**
(and, internally, occasionally games). Every VRseBuilder app is organized as:

```
Project                              (Assets/StudioProjects/<ProjectName>/)
└── Module                           (a unit of content, e.g. "Machine Safety")
    ├── Training experience          (the learner walkthrough)
    └── Evaluation experience        (the graded assessment)
```

- **Project** — the top-level app. Lives under `Assets/StudioProjects/<ProjectName>/`,
  tracked by a `RoomManagerConfig` asset. A project is either a **local** project already on
  disk, or an **accessible** project from the AutoVRse backend (requires login).
- **Module** — a content unit inside a project (e.g. a machine, a procedure, a scenario).
- **Experience** — every module has exactly **two** experiences: `Training` and
  `Evaluation`. Each experience has its own dev scene (where the Story lives) and, usually,
  an art scene (the 3D environment the story's interactions reference).

Creating a **new local Project** is supported through `vrse/create-project` when no
selectable local project exists. It creates the project folder and the required
`ProjectConfig`, `RoomManagerConfig`, and `BrandManagerConfig` assets, then selects the new
project. After creating a local project, run `vrse/create-menu-scene` so
`RoomManagerConfig.MainMenuScene` points at the copied default menu scene and Room Configs
can be applied cleanly. These creation tools require explicit intent because they write
assets. Backend project creation is still done outside this local MCP flow.

## Tool order — do not skip steps

Read each tool's own MCP description for its full parameter contract; this is the sequence
and *why* each step exists.

1. **`vrse/status`** — always start here. Tells you `loggedIn`, `selectedProject`, the active
   Unity scene, and whether backend support is even compiled in (`backendEnabled`). If
   `backendEnabled` is false, skip login/backend steps entirely — only local projects are
   usable.
2. **`vrse/login`** — only if you need **backend-accessible** projects (not just local ones)
   and `loggedIn` is false. Needs `username`/`password`. Skip if the user only wants a local
   project that's already on disk, or if `backendEnabled` is false.
3. **`vrse/list-projects`** — returns both `accessibleProjects` (backend, needs login) and
   `localProjects` (configured local projects with a `RoomManagerConfig` under
   `Assets/StudioProjects/<ProjectName>/ProjectSettings/`). Use this to find the exact
   project name/id before selecting — don't guess a project name. Bare folders such as
   art dumps or default placeholders may be reported as `ignoredLocalProjectFolders`, but
   they are not selectable projects.
4. **`vrse/create-project`** — only when no suitable local project exists and the user asked
   you to create one. Pass a single valid folder name and `confirm: true`. The created project
   is selected automatically.
5. **`vrse/select-project`** — sets the active project by `projectName` or `projectId`. **Every
   subsequent vrse/* call that needs a project will use this selection implicitly** if you
   don't pass `projectName` explicitly. Always select before doing module/experience work.
6. **`vrse/ensure-project-settings`** — run once after selecting a project you haven't worked
   in this session, especially a freshly-created one.
7. **`vrse/create-menu-scene`** — for a freshly-created local project, create/assign the
   default menu scene before applying project settings. It copies the package menu scene to
   `Assets/VRseBuilder/MenuScene/MenuGlassUI.unity`, assigns `RoomManagerConfig.MainMenuScene`,
   and applies project settings by default. Use `overwrite: true` only when you intentionally
   want to replace the managed menu scene contents.
8. **`vrse/apply-project-settings`** — run after the menu scene exists, or rely on
   `vrse/create-menu-scene`'s default `applySettings: true`. These create/sync the project's
   local settings (render pipeline, layers, Room Configs, etc.) — skipping this can leave a
   new project half-configured.
9. **`vrse/list-modules`** — lists modules for the selected project, each with its
   `Training`/`Evaluation` experiences (id, name, jsonFileUrl if backend-known). Use this to
   check whether the module/experience you want already exists before creating it.
10. **`vrse/create-experience`** — creates a module (if it doesn't exist yet) **and** one of
   its two experiences' dev scene in one call. Requires `moduleName`, `experienceName`, and
   either `jsonFileUrl` (backend story JSON URL) or enough info to resolve one from the
   logged-in backend project. Pass `experienceType: "Training"` or `"Evaluation"` — defaults
   to `Training` if omitted. Call it **twice** (once per type) if the module needs both.
11. **`vrse/open-module`** / **`vrse/open-art-scene`** — open the module's dev scene or its art
   scene once created, so subsequent story/interactable work has the right scene loaded.
   `vrse/get-experience-creation-status` reports what's been created so far without opening
   anything.

## Related, situational tools

- **`vrse/get-selected-project`**, **`vrse/get-project-config`** — read-only lookups; use
  instead of re-running `list-projects` when you already know the project.
- **`vrse/create-menu-scene`**, **`vrse/open-menu-scene`**, **`vrse/open-room-manager-config`**
  — create/assign the default menu scene, open a project's menu scene, or inspect its
  `RoomManagerConfig` asset.
- **`vrse/create-evaluation-from-training`** — clones a Training experience's story into a
  new Evaluation experience instead of building the evaluation from scratch. Prefer this over
  a bare `vrse/create-experience` call when the evaluation should mirror the training content.
- **`vrse/module-set-include-in-build`**, **`vrse/build-start`**, **`vrse/build-status`** —
  once modules/experiences are ready, these control what ships in the build. Out of scope for
  authoring — only reach for these when the user explicitly asks about building.
- **`vrse/open-studio-project-window`**, **`vrse/open-project-config-window`**,
  **`vrse/open-build-tool`** — open the human-facing editor windows for the same workflows,
  for when the user wants to do something the scripted tools don't cover (e.g. creating a
  brand-new Project).

## Pitfalls

- **Calling module/story/interactable tools without selecting a project first.** Most vrse/*
  tools resolve the project from the last `vrse/select-project` call (via `EditorPrefs`, so it
  persists across calls in the same Editor session) — if a fresh Editor session has none
  selected, they'll return `"No project selected"`. Select explicitly rather than assuming.
- **Assuming `vrse/create-experience` makes a new Project.** It only creates a Module +
  Experience *inside* the already-selected Project. Use `vrse/create-project` first when a
  local Project does not exist.
- **Skipping `ensure-project-settings`/`apply-project-settings` on a new project.** A project
   that was just created or hasn't been opened in this Editor before can be missing local
   settings (URP quality assets, layers) that later story/interactable tools implicitly rely
   on — apply them once, up front.
- **Applying settings before a menu scene exists.** Room Configs are skipped when
  `RoomManagerConfig.MainMenuScene` is empty. On fresh local projects, run
  `vrse/create-menu-scene` before, or instead of, a separate `vrse/apply-project-settings`
  call.
- **Not distinguishing Training vs Evaluation.** They're two separate experiences with two
  separate dev scenes and story JSON files under the same module — always confirm which one
  (or both) the user means before creating or editing.

Once a Module/Experience exists and its dev scene is open, hand off to
**`vrse-story-editing`** to build the Story, and **`vrse-interactables`** to prep the art
scene's grabbable/touchable/placepoint objects the story will reference. If the module is a
procedural/training experience (or it's unclear what shape it should be), check
**`vrse-level-design`** first for scene-organization and scaffolding conventions before either
of those.
