# Multi-stage build: SDK for compilation, runtime for execution
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Copy solution and project files
COPY . .

# Restore dependencies
RUN dotnet restore

# Build and publish
RUN dotnet publish -c Release -o /app/publish --no-restore

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app

# Create persistent data directory
RUN mkdir -p /app/data

# Copy published application from build stage
COPY --from=build /app/publish .

# Expose port for API
EXPOSE 5000

# Health check
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl --fail http://localhost:5000/api/sync || exit 1

# Use non-root user
# aspnet base image has 'app' user at 1000, rename it to watchback
RUN usermod -l watchback app && usermod -d /home/watchback -m watchback && chown -R watchback:watchback /app
USER watchback

# Set environment for SQLite database location
ENV ASPNETCORE_URLS=http://+:5000

# Run application
ENTRYPOINT ["dotnet", "WatchBack.Api.dll"]
