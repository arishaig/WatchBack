# Contributing to WatchBack

Thanks for your interest in improving WatchBack. This document covers how to get
set up, the conventions the project follows, and what a good pull request looks
like.

By participating in this project you agree to abide by the
[Code of Conduct](CODE_OF_CONDUCT.md).

## Getting started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 22+](https://nodejs.org/) and npm (for the frontend)
- Docker (optional, for container builds)

### Build and run

```bash
# Restore and build the solution
dotnet build

# Run the API (serves the built frontend)
dotnet run --project src/WatchBack.Api

# Frontend dev server with watch mode (from repo root)
npm install
npm run dev
```

Kestrel prints the listen URL on startup (the default is
`http://localhost:5000`). Open it and you'll be prompted to set a username and
password on first run. The container image instead listens on port 8484.

### Tests

```bash
# All .NET tests
dotnet test

# A single project
dotnet test tests/WatchBack.Core.Tests

# Frontend unit tests
npm test
```

Live integration tests in `WatchBack.Infrastructure.Tests` are opt-in and
require real credentials in a local `.env` file. They are skipped by default.

## Project layout

```
src/
  WatchBack.Core/            Interfaces, models, options, orchestration
  WatchBack.Infrastructure/  Provider implementations, EF Core persistence
  WatchBack.Api/             ASP.NET Core host, endpoints, auth, static frontend
  WatchBack.Resources/       Localization .resx files (en, es)
tests/
  WatchBack.*.Tests/
frontend/                    TypeScript + Alpine.js + Tailwind, built with Vite
```

See [`CLAUDE.md`](CLAUDE.md) for a fuller architecture overview.

## Coding conventions

The `.editorconfig` enforces style at build time
(`EnforceCodeStyleInBuild = true`), so `dotnet build` will fail on violations.
Run `dotnet format` before committing.

**C#** follows the Microsoft C# coding conventions and the .NET Runtime team
style guide. Highlights:

- File-scoped namespaces, 4-space indentation, Allman braces
- Nullable reference types and implicit usings are on project-wide
- `string`/`int`, not `String`/`Int32`; no `this.` qualification
- Primary constructors preferred; explicit visibility modifiers always present
- Private instance fields `_camelCase`; private static fields `s_camelCase`

**TypeScript** (`frontend/`) is strict mode, follows the Microsoft TypeScript
guidelines: no `I` prefix on interfaces, PascalCase types, camelCase
members, double quotes, 4-space indentation, K&R braces.

## Branching and commits

- Create a feature branch off the latest `main` for each logical change.
- Keep commits small and targeted — touch as few files per commit as possible.
- Write clear commit messages; conventional-commit prefixes
  (`fix:`, `feat:`, `chore:`, `ci:`, `docs:`, `build(deps):`) are used in
  history and appreciated.
- Before opening a PR, pull the latest `main` and merge it into your branch.

## Bug fixes require a regression test

A bug fix must include a test that fails before the fix and passes after, in the
**same commit** as the fix. PRs that change behavior without a covering test
will be asked to add one.

## Adding a new provider

WatchBack is built around a provider pattern. To add a watch state source,
thought source, or ratings source:

1. Add an options class in `WatchBack.Core/Options/` if it needs configuration.
2. Implement the relevant interface (`IWatchStateProvider`, `IThoughtProvider`,
   `IMediaSearchProvider`, `IRatingsProvider`) in `WatchBack.Infrastructure/`.
3. Expose metadata via the matching `*ProviderMetadata` record.
4. Implement `GetServiceHealthAsync()` and `GetConfigSchema()`.
5. Register it in
   `WatchBack.Infrastructure/Extensions/ServiceCollectionExtensions.cs`.
6. Bind the options in `Program.cs`.

Look at the existing implementations and the interface XML docs in
`WatchBack.Core/Interfaces` for the full contract.

## Opening a pull request

1. Make sure `dotnet build`, `dotnet test`, and `npm test` all pass.
2. Run `dotnet format`.
3. Fill out the pull request template.
4. Link any related issue.

On every PR, CI runs the .NET build, the unit/integration and accessibility
tests, a TypeScript type-check, the frontend tests, a multi-arch Docker build,
CodeQL, a dependency audit (npm + NuGet), and a Dockerfile lint. Please keep the
build green. (Trivy container scanning runs on pushes to `main` and on a
weekly schedule, not on PRs.)

## Reporting security issues

Do not open a public issue for security problems. Follow the process in
[`SECURITY.md`](SECURITY.md).
