# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**WatchBack** is a self-hosted .NET web application that tracks what you're currently watching (movies/TV shows) and aggregates community discussion and ratings from multiple sources. It uses a provider-based architecture with a .NET backend and an Alpine.js + Tailwind CSS frontend.

- **.NET Target**: .NET 10.0 (`LangVersion: latest`)
- **Frontend**: Alpine.js 3.15, Tailwind CSS 4.2, built with Vite 8
- **Database**: SQLite via EF Core 10
- **Authentication**: Cookie-based (30-day sliding) with optional reverse-proxy forward auth
- **Localization**: en-US (default), es — via .resx files in WatchBack.Resources
- **Containerization**: Multi-stage Dockerfile, port 8484

## Architecture

### Solution Structure

```
src/
  WatchBack.Core/          — Interfaces, models, options, core services (SyncService, ReplyTreeBuilder, TimeMachineFilter)
  WatchBack.Infrastructure/ — Provider implementations, EF Core persistence, HTTP resilience
  WatchBack.Api/           — ASP.NET Core host, endpoints, auth, logging, static frontend
  WatchBack.Resources/     — Localization .resx files (en, es)
tests/
  WatchBack.Core.Tests/
  WatchBack.Infrastructure.Tests/
  WatchBack.Api.Tests/     — Integration tests + Playwright E2E + axe-core accessibility
```

### Layer Responsibilities

**WatchBack.Core** (library) — Contracts and orchestration logic. No external dependencies beyond Microsoft.Extensions abstractions.
- `Interfaces/`: Provider contracts — `IWatchStateProvider`, `IThoughtProvider`, `IMediaSearchProvider`, `IRatingsProvider`, `IManualWatchStateProvider`, `ISyncService`, `IReplyTreeBuilder`, `ITimeMachineFilter`, `IPrefetchService`
- `Models/`: Immutable C# records — `MediaContext`, `EpisodeContext`, `Thought`, `ThoughtResult`, `SyncResult`, `MediaSearchResult`, `MediaRating`, `BrandData`, `ServiceHealth`
- `Options/`: Strongly-typed config — `WatchBackOptions`, `AuthOptions`, `BlueskyOptions`, `JellyfinOptions`, `OmdbOptions`, `RedditOptions`, `TraktOptions`
- `Services/`: `SyncService` (orchestrator), `ReplyTreeBuilder`, `TimeMachineFilter`

**WatchBack.Infrastructure** (library) — Concrete provider implementations and data access.
- `WatchStateProviders/`: Jellyfin, Trakt, Manual (singleton — holds state in memory)
- `ThoughtProviders/`: Reddit (via PullPush), Bluesky, Trakt
- `MediaSearchProviders/`: OMDb (implements both `IMediaSearchProvider` and `IRatingsProvider`)
- `Http/`: `ResilientHttpHandler` — retry with exponential backoff, 429 rate-limit awareness
- `Persistence/`: `WatchBackDbContext` (SQLite), entities, migrations, repository
- `Services/`: `PrefetchService` — background cache warming for next episodes

**WatchBack.Api** (web host) — HTTP surface, DI composition, and frontend hosting.
- `Program.cs`: DI registration, middleware pipeline, SQLite + user-settings.json config
- `Endpoints/`: Minimal API route groups — Sync, Config, Auth, Search, ManualWatchState, Diagnostics, System, Strings
- `Auth/`: `ForwardAuthHandler` — optional reverse-proxy auth with IP pinning
- `Logging/`: `InMemoryLogBuffer` (circular, 500 entries), SSE streaming
- `Serialization/`: Source-generated `WatchBackJsonContext`

### Key Design Patterns

- **Provider pattern**: Multiple `IThoughtProvider` / `IRatingsProvider` implementations registered via DI; `IWatchStateProvider` selected by user config
- **Options pattern**: `IOptionsSnapshot<T>` for scoped providers, `IOptionsMonitor<T>` for singletons
- **Metadata pattern**: Every provider exposes `*ProviderMetadata` (name, description, display name, brand data)
- **Records for models**: All data transfer objects are immutable C# records
- **Source-generated JSON**: `WatchBackJsonContext` for AOT-friendly serialization
- **SSE streaming**: Real-time sync progress and log tailing

## Common Commands

```bash
# Build solution
dotnet build

# Build in Release mode (includes frontend via Vite)
dotnet build -c Release

# Run the API (development)
dotnet run --project src/WatchBack.Api

# Run all tests
dotnet test

# Run specific test project
dotnet test tests/WatchBack.Core.Tests

# Format code (applies .editorconfig rules)
dotnet format

# Frontend dev (watch mode, from repo root)
npm run dev

# Frontend tests
npm test

# EF Core migrations (from repo root)
dotnet ef migrations add <Name> --project src/WatchBack.Infrastructure --startup-project src/WatchBack.Api
```

## Code Style and Conventions

- **File-scoped namespaces**: `namespace X.Y;` (not block-scoped)
- **Implicit usings** and **nullable reference types**: enabled project-wide
- **EditorConfig enforced at build**: `EnforceCodeStyleInBuild = true` — style violations fail the build
- **Indentation**: 4 spaces for C#, 2 spaces for `.csproj` / `.props` / `.json` / `.xml`
- **Imports**: System directives first, groups separated by blank line
- **Naming**: PascalCase types/methods/properties, camelCase locals/params, `_camelCase` private fields, `s_camelCase` private static fields, `I` prefix for interfaces, `T` prefix for type parameters
- **No `this.` qualification**
- **Predefined types**: `string` not `String`, `int` not `Int32`
- **Primary constructors**: preferred (`csharp_style_prefer_primary_constructors = true`)

## Key Implementation Patterns

### Adding a new provider

1. Define an options class in `WatchBack.Core/Options/` if the provider needs configuration
2. Implement the appropriate interface in `WatchBack.Infrastructure/` (e.g., `IThoughtProvider`, `IWatchStateProvider`)
3. Expose metadata via the matching `*ProviderMetadata` record
4. Implement `GetServiceHealthAsync()` for health checks and `GetConfigSchema()` for UI-driven configuration
5. Register in `WatchBack.Infrastructure/Extensions/ServiceCollectionExtensions.cs`
6. Bind options in `Program.cs`

### Provider lifetime conventions

- **Watch state providers**: Scoped (except `ManualWatchStateProvider` which is singleton)
- **Thought providers**: Scoped
- **Media search / ratings providers**: OMDb is singleton (uses `IOptionsMonitor`)
- Scoped providers capture `IOptionsSnapshot<T>.Value` at construction; singleton providers use `IOptionsMonitor<T>.CurrentValue` inline

## Testing

- **Framework**: xUnit + FluentAssertions + NSubstitute
- **Coverage**: coverlet.collector
- **API tests**: `Microsoft.AspNetCore.Mvc.Testing` (WebApplicationFactory)
- **E2E**: Playwright + Deque axe-core for accessibility
- **Convention**: `[ProjectName].Tests` in `/tests/`
- **Live integration tests** in Infrastructure.Tests are opt-in (require real `.env` config)
