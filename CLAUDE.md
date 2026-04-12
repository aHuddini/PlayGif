# PlayGif Developer Guide

Playnite extension for animated GIF display. C# / .NET 4.6.2 / WPF.

## Build

```bash
dotnet clean -c Release
dotnet build -c Release
powershell -ExecutionPolicy Bypass -File scripts/package_extension.ps1
```

Always run all three steps in order. Version is read from `version.txt`.

## Architecture

Service-oriented with MVVM. Entry point: `PlayGif.cs`. Settings: `PlayGifSettings.cs`.

- **Source layout**: All C# source in `src/`. Build output: `src/bin/Release/net4.6.2/PlayGif.dll`
- **Version source of truth**: `version.txt` — packaging script reads from here, overwrites `extension.yaml`

## Conventions

- **Naming**: PascalCase public, `_camelCase` private fields, `I`-prefix interfaces
- **Comments**: Single-line `//` (not `/// <summary>`). Inline `//` for enums/simple props. Remove if name is self-documenting. Only use XML docs for public APIs needing `<param>`/`<returns>`
- **Constants**: All in `Common/Constants.cs` with `#region` grouping
- **Logging**: `Logger.*` (Playnite SDK → extension.log). Use sparingly — one `Logger.Info` at startup, `Logger.Error` for failures.

## Key Patterns

- Settings: property in `PlayGifSettings.cs` with `OnPropertyChanged()`, UI in `PlayGifSettingsView.xaml`
- Menu extension: `GetGameMenuItems()` in `PlayGif.cs` — branches on single-game vs multi-game selection
- Handler pattern for large operations (extract to `Handlers/` or `Services/`)

## Workflow

### Planning & Execution
- **Plan Mode Default**: Use plan mode for any non-trivial task. Research the codebase, draft a plan, get approval before coding.
- **Subagent Strategy**: Use subagents for parallel independent research. Don't duplicate work between main context and subagents.
- **Verification Before Done**: Run build + package after every code change. Never claim "done" without verified output.
- **Autonomous Bug Fixing**: If you introduce a bug, fix it immediately without asking. If a build fails, diagnose and fix before proceeding.

### Task Management
1. **Plan First** — Break work into small, trackable steps
2. **Verify Plan** — Confirm approach before writing code
3. **Track Progress** — Use TodoWrite to show progress on multi-step tasks
4. **Explain Changes** — Summarize what changed and why
5. **Document Results** — Report build/test outcomes
6. **Capture Lessons** — Update memory files with reusable insights

### Core Principles
- **Simplicity First**: Minimal changes to achieve the goal. No over-engineering, no speculative features, no unnecessary abstractions.
- **No Laziness**: Don't skip steps, don't leave TODOs, don't use placeholders. Complete the work fully.
- **Minimal Impact**: Change only what's needed. Don't refactor surrounding code, don't add comments to unchanged lines, don't "improve" things that weren't asked for.
- **Demand Elegance**: Code should be clean and idiomatic. Prefer readable solutions over clever ones.

## Documentation Update Rules

When updating documentation for a new version:

- **README.md** ("What's New"): User-facing, non-technical. Focus on what users experience. No class names, no architecture details. Only show ONE previous version summary.
- **CHANGELOG.md**: Developer-facing, technical detail welcome. Use sections: Fixed, Added, Changed, Performance.
- **Manifest/installer.yaml**: Concise changelog items with `[Category]` prefixes (`[Critical Fix]`, `[New Feature]`, `[Performance]`, `[Improved]`). Add new version entry BEFORE existing entries.

## References

- [Playnite SDK Documentation](https://playnite.link/docs/)
- [UPS Project](../UniPSound/UniPlaySong/) — Reference implementation for patterns and conventions
