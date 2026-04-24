# BranchedTabs for Visual Studio

BranchedTabs is a Visual Studio extension that keeps parts of your workspace state separate per Git branch.

Current features:
- restore open document tabs per branch
- restore startup project selection per branch
- support single startup project and multi-startup project configurations
- preserve multi-startup project order and action values
- support standard Git repositories and Git worktrees
- track detached HEAD states separately

## What it does

When you open a solution inside a Git repository, the extension watches the current branch and stores branch-specific workspace state.

When you switch branches, BranchedTabs can restore:
- the files you had open for that branch
- the startup project configuration you had for that branch

This lets different branches keep different working contexts without manually reopening files or reconfiguring startup projects every time.

## Features

### Branch-based tabs

When enabled, the extension stores the list of currently open documents for the active branch.

When the branch changes, it can restore those tabs using one of the configured restore modes.

Supported tab restore modes:
- `RestoreOnly`
  - opens the saved tabs for the new branch
  - does not close any currently open documents
- `ReplaceAndKeepUnsaved`
  - closes open saved documents
  - keeps unsaved documents open
  - opens the saved tabs for the new branch
- `ReplaceAllAndSaveUnsaved`
  - saves unsaved documents
  - closes all open documents
  - opens the saved tabs for the new branch

### Branch-based startup projects

When enabled, the extension stores the startup configuration for the active branch.

Supported startup configurations:
- single startup project
- multi-startup project configurations

For multi-startup configurations, the extension stores:
- the participating projects
- the configured order
- the configured action value for each project

On restore, projects are matched in this order:
1. full project path
2. Visual Studio unique project name
3. project name

## Visual Studio Options

Options are available under:
- `Tools > Options > Branched Tabs > General`

Current settings:

### Enable Branch-Based Tabs
When enabled, open document tabs are saved and restored per Git branch for Git-backed solutions.

Notes:
- detached HEAD states are tracked separately
- Git worktrees are supported

### Enable Branch-Based Startup Projects
When enabled, startup project selections are saved and restored per Git branch for Git-backed solutions.

Notes:
- supports single-project and multi-project startup configurations
- includes multi-startup order and action values
- if a project moved or was renamed, restore uses path first and then name-based fallback

### Restore Mode
Controls how tabs are restored when switching branches.

This affects only the tab feature, not startup project restore.

## How it works

### Tab behavior

The tab feature reads currently open documents from Visual Studio and stores their full file paths per branch.

Behavior summary:
- on solution open, it loads saved state and restores tabs for the current branch
- before solution close, it saves open tabs for the current branch
- on branch change, it saves the previous branch tab set and restores the new branch tab set

Only documents that exist on disk are tracked and restored.

### Startup project behavior

The startup project feature reads and writes the Visual Studio solution startup configuration through the Visual Studio automation model.

Behavior summary:
- on solution open, it attempts to restore saved startup configuration for the current branch
- before solution close, it saves the current startup configuration for the current branch
- on branch change, it saves the previous branch configuration and restores the current branch configuration

Matching behavior:
- path match is preferred because it is the strongest identity
- if the path no longer matches, the extension falls back to unique name and then project name

Current restore behavior:
- if no saved startup configuration exists for the branch, nothing is changed
- if no saved projects can be matched, nothing is changed
- if some saved multi-startup entries can be matched, the matched entries are restored in saved order

## Git behavior

The extension works only for solutions inside Git repositories.

Supported repository layouts:
- normal repositories where `.git` is a directory
- worktree-style repositories where `.git` is a file pointing to the actual Git directory

Detached HEAD handling:
- detached HEAD is stored separately from named branches
- detached states are tracked using the current commit reference in the Git `HEAD` file

## State storage

State is stored per solution under:
- `.vs/BranchTabs.json`

The file stores branch state for both features:
- open tabs
- startup project configuration

Backward compatibility:
- older tab-only state files are automatically read and migrated in memory to the newer combined state format

## Compatibility

### Target framework
- .NET Framework 4.7.2

### Visual Studio support
The VSIX manifest is configured for:
- Visual Studio 2022 (`17.x`)
- Visual Studio 2026 (`18.x`)

Manifest version range:
- installation target: `[17.0, 19.0)`
- prerequisite range: `[17.0,19.0)`

### Separate builds
A separate build should not be necessary as long as the used Visual Studio SDK and automation APIs remain compatible across both versions.

The project is currently set up as a single VSIX targeting both version ranges.

## Installation

1. Download the `.vsix` from [Releases](https://github.com/MikeKatsoulakis/BranchedTabs/releases).
2. Run the installer.
3. Restart Visual Studio.

The extension is intended for 64-bit Visual Studio installations.

## Building

Open the solution in Visual Studio and build the `BranchedTabs` project.

Project characteristics:
- VSIX project
- AsyncPackage-based extension
- targets .NET Framework 4.7.2
- uses `Microsoft.VisualStudio.SDK`
- uses `Newtonsoft.Json` for state persistence

For local development, use the Visual Studio experimental instance.

## Project structure

Main files:
- `BranchedTabs/BranchedTabsPackage.cs`
  - package entry point
  - initializes feature managers
- `BranchedTabs/TabManager.cs`
  - branch-based tab tracking and restore
- `BranchedTabs/StartupProjectManager.cs`
  - branch-based startup project tracking and restore
- `BranchedTabs/GitBranchContext.cs`
  - shared Git repository detection, branch lookup, worktree support, and branch watcher
- `BranchedTabs/BranchWorkspaceState.cs`
  - persisted state model and load/save logic
- `BranchedTabs/TabManagerOptions.cs`
  - Visual Studio options page
- `BranchedTabs/TabRestoreMode.cs`
  - tab restore behavior enum

## Known limitations and caveats

- The extension is Git-only. Non-Git solutions are ignored.
- Startup project restore depends on what Visual Studio exposes through the automation model.
- Missing or renamed projects may restore through fallback matching, but not every solution change can be resolved perfectly.
- If no matching startup projects are found, startup restore is skipped.
- If only part of a saved multi-startup configuration can be matched, only the matched subset is restored in saved order.
- Tab and startup state are saved as best-effort operations. Failures are intentionally silent to avoid disrupting normal IDE usage.
- State is stored per solution under the `.vs` folder, so it is local machine state and should typically not be committed.

## Contributing

Contributions are welcome.

Suggested workflow:
1. Fork the repository.
2. Clone it locally.
3. Open the solution in Visual Studio.
4. Build and test in the experimental instance.
5. Submit a pull request.

## License

This project is licensed under the [MIT License](LICENSE).

## Issues and feedback

For bugs, feature requests, or suggestions, open an [issue](https://github.com/MikeKatsoulakis/BranchedTabs/issues).
