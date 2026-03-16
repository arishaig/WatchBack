# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**WatchBackNet** is a .NET solution that provides a provider-based architecture for watch state tracking and comment data retrieval. It's designed to support multiple data sources through pluggable provider implementations.

- **.NET Target**: .NET 10.0
- **Architecture Pattern**: Provider pattern with dependency abstraction
- **Build System**: dotnet CLI (MSBuild)

## Architecture

### Project Structure

**WatchBack.Core** (library)
- Contains all interfaces and models that define the contract
- `Interfaces/`: Core provider interfaces
  - `IDataProvider`: Base interface for all providers with health checks
  - `IWatchStateProvider`: Retrieves current media context (movies/shows currently being watched)
  - `ICommentDataProvider`: Retrieves user thoughts/comments about media
- `Models/`: Data contracts using C# records
  - `ServiceHealth`: Provider health status
  - `MediaContext` / `EpisodeContext`: Media information
  - `Thought` / `ThoughtResult`: Comment/thought data structures
  - `BrandData`: UI branding information for providers

**WatchBack.Infrastructure** (library)
- Implementation layer - not yet populated with implementations
- Should contain concrete implementations of Core interfaces

### Design Patterns

- **Provider Pattern**: Multiple implementations of IDataProvider can be registered and used
- **Metadata Pattern**: Each provider exposes metadata (name, description, display name) via DataProviderMetadata
- **Records for Models**: All data models are immutable C# records

## Common Commands

```bash
# Build the solution
dotnet build

# Build in Release mode
dotnet build -c Release

# Clean build artifacts
dotnet clean

# Format code (applies .editorconfig rules)
dotnet format

# Run solution (when applicable)
dotnet run --project src/[ProjectName]
```

## Code Style and Conventions

- **File-scoped namespaces**: All files use `namespace [path];` format (not braces)
- **Implicit usings**: Enabled via ImplicitUsings in project files
- **Nullable reference types**: Enabled project-wide
- **EditorConfig**: Enforced via .editorconfig at root - includes:
  - 4-space indentation for C# files
  - 2-space indentation for project files (*.csproj, *.props)
  - Organized imports with System directives first, then groups separated
  - No `this.` qualification for fields/properties
  - Predefined types (e.g., `string` not `String`) in code
- **Code analysis**: EnforceCodeStyleInBuild enabled - builds fail if code style violations exist

## Key Implementation Patterns

When adding new providers:

1. Create metadata record extending `DataProviderMetadata` (or `WatchStateDataProviderMetadata` / `CommentDataProviderMetadata`)
2. Implement the appropriate interface (IWatchStateProvider, ICommentDataProvider)
3. Implement `GetServiceHealthAsync()` for health checks
4. Place implementations in WatchBack.Infrastructure

Example:
```csharp
public class MyWatchStateProvider : IWatchStateProvider
{
    public DataProviderMetadata Metadata => new WatchStateDataProviderMetadata(
        Name: "MyProvider",
        Description: "Provides watch state from my service");

    public Task<ServiceHealth> GetServiceHealthAsync() { /* ... */ }
    public Task<MediaContext> GetCurrentMediaContextAsync() { /* ... */ }
}
```

## Testing

Tests directory is currently empty. When adding tests:
- Create test projects following [ProjectName].Tests naming
- Use standard .NET test frameworks (xUnit, NUnit, or MSTest)
- Place in `/tests/` directory

## Notes for Implementation

- The solution is in early scaffolding phase - Infrastructure implementations are pending
- Two placeholder Class1.cs files exist in both projects (can be removed)
- Empty Services folder in Core is available for service abstractions if needed
- No NuGet dependencies currently specified - add as needed for implementations
