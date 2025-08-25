# Repository Guidelines

## Project Structure & Module Organization
- `darkorbit_resource_downloader/`: C# console app source (entry: `Program.cs`).
- `darkorbit_resource_downloader/bin`, `obj`: build artifacts (ignored).
- `darkorbit_resource_downloader.sln`: solution file.
- `.github/workflows/dotnet.yml`: CI for build/test/publish across RIDs.
- Runtime downloads are saved under `do_resources/` (created at run time).

## Build, Test, and Development Commands
- Restore: `dotnet restore darkorbit_resource_downloader.sln` — restore packages.
- Build: `dotnet build darkorbit_resource_downloader.sln -c Release` — compile all projects.
- Run (local): `dotnet run --project darkorbit_resource_downloader` — prompts for resource XML and downloads assets.
- Publish (RID-specific):
  - `dotnet publish darkorbit_resource_downloader/darkorbit_resource_downloader.csproj -c Release -r win-x64 -o publish/win-x64`
  - Replace `win-x64` with `linux-x64`, `osx-x64`, or `osx-arm64`.
- CI: pushes to `master` build/test and create tagged releases with zipped artifacts.

## Coding Style & Naming Conventions
- Indentation: tabs (visual width 4) to match existing files.
- C# conventions: PascalCase for types/properties; camelCase for locals/parameters.
- Private fields: underscore-prefixed (e.g., `_skippedFiles`).
- Language version: `latest` (`net9.0`). Prefer `var` when type is obvious.
- Formatting: run `dotnet format` before PRs (no custom analyzers configured).

## Testing Guidelines
- Framework: none checked in yet; CI runs `dotnet test` if tests exist.
- Recommended: add `Xunit` test projects named `ProjectName.Tests` alongside the main project.
- Naming: test methods describe behavior (e.g., `Downloads_File_When_Missing`).
- Run: `dotnet test darkorbit_resource_downloader.sln`.

## Commit & Pull Request Guidelines
- Commits: use imperative, concise summaries (e.g., "Fix CI", "Add multi-XML download").
- Scope: keep changes focused; include rationale in the body if non-trivial.
- PRs: include a clear description, linked issues, steps to validate (commands/output), and note any CI or RID-related impacts.

## Security & Configuration Tips
- Network access: downloader hits official DarkOrbit endpoints; avoid changing hosts without review.
- Artifacts: do not commit `do_resources/` outputs; consider adding it to `.gitignore` locally.
- Concurrency: downloads run in parallel (degree 10). Be mindful of rate limits when modifying.

## Git Hooks
- Pre-commit formatting: `.githooks/pre-commit` runs `dotnet format` on `darkorbit_resource_downloader.sln`.
- Enable hooks: `bash scripts/setup-git-hooks.sh` (or PowerShell: `./scripts/setup-git-hooks.ps1`).
