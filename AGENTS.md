# Repository Guidelines

## Project Structure & Module Organization
This repository contains a WinUI 3 desktop app in `NOCREPORTGENERATOR/` and a lightweight Node dependency manifest at the repo root.

- `NOCREPORTGENERATOR/Pages/`: UI pages (`*Page.xaml` + code-behind).
- `NOCREPORTGENERATOR/Services/`: application services (imports, parsing, settings, diagnostics).
- `NOCREPORTGENERATOR/Models/`: domain/data models.
- `NOCREPORTGENERATOR/Assets/`: icons, fonts, animations, and app imagery.
- `NOCREPORTGENERATOR.slnx`: solution entry point.
- `package.json`: font/icon package dependency (`remixicon`).

## Build, Test, and Development Commands
Run commands from the repository root.

- `dotnet restore NOCREPORTGENERATOR.slnx`: restore NuGet packages.
- `dotnet build NOCREPORTGENERATOR.slnx -c Debug`: compile the app.
- `dotnet run --project NOCREPORTGENERATOR/NOCREPORTGENERATOR.csproj`: launch locally.
- `dotnet publish NOCREPORTGENERATOR/NOCREPORTGENERATOR.csproj -c Release -r win-x64`: create a release build.
- `npm install`: restore Node dependency used for icon/font assets.

## Coding Style & Naming Conventions
Use existing C# conventions already present in this codebase.

- Indentation: 4 spaces, UTF-8 text, nullable reference types enabled.
- Types/methods/properties: `PascalCase`.
- Private fields: `_camelCase` (example: `_currentTag`).
- Keep XAML and code-behind names aligned (example: `DashboardPage.xaml` and `DashboardPage.xaml.cs`).
- Put feature logic in `Services/`; keep page code-behind focused on UI orchestration.

## Testing Guidelines
There is currently no dedicated test project in this repository.

- Before opening a PR, run `dotnet build` and perform a manual smoke test of key flows:
- navigation between pages,
- TT import/refresh actions,
- settings persistence and log viewer behavior.
- When adding non-trivial service logic, create a companion test project (for example `NOCREPORTGENERATOR.Tests`) and name tests `MethodName_State_ExpectedResult`.

## Commit & Pull Request Guidelines
Recent history uses inconsistent commit subjects (`commit`, `COMMIT`, etc.). Use a clearer standard going forward.

- Commit message format: `type(scope): imperative summary` (example: `fix(import): handle canceled token safely`).
- Keep commits focused and runnable.
- PRs should include:
- concise description of change and intent,
- linked issue/task ID when available,
- screenshots or short video for UI changes,
- verification notes listing commands executed and manual checks performed.
