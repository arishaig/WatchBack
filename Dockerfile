# syntax=docker/dockerfile:1

# ──────────────────────────────────────────
# Stage 1: Frontend build (Vite / npm)
# ──────────────────────────────────────────
FROM node:22-alpine AS frontend

WORKDIR /app

# Install dependencies first — layer is cached until package files change
COPY package.json package-lock.json ./
RUN --mount=type=cache,target=/root/.npm \
    npm ci --prefer-offline

# Copy Vite config and TypeScript sources
COPY vite.config.ts ./
COPY frontend/ ./frontend/

# index.html is scanned by Tailwind (@source directive in tw.css)
COPY src/WatchBack.Api/wwwroot/index.html ./src/WatchBack.Api/wwwroot/index.html

RUN npm run build

# ──────────────────────────────────────────
# Stage 2: .NET build & publish
# ──────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Copy project files first so the restore layer is cached independently
# of source changes — only invalidated when a .csproj or .sln changes.
COPY *.sln .
COPY src/WatchBack.Api/*.csproj         src/WatchBack.Api/
COPY src/WatchBack.Core/*.csproj        src/WatchBack.Core/
COPY src/WatchBack.Infrastructure/*.csproj src/WatchBack.Infrastructure/
COPY src/WatchBack.Resources/*.csproj   src/WatchBack.Resources/

RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore src/WatchBack.Api/WatchBack.Api.csproj

# Copy source and freshly built frontend assets
COPY src/ src/

COPY --from=frontend /app/src/WatchBack.Api/wwwroot/js/app.js \
                          src/WatchBack.Api/wwwroot/js/app.js
COPY --from=frontend /app/src/WatchBack.Api/wwwroot/css/app.bundle.css \
                          src/WatchBack.Api/wwwroot/css/app.bundle.css

# Publish — the NuGet cache mount makes the implicit re-restore near-instant
# by serving packages from the BuildKit cache without re-downloading them.
# (--no-restore is intentionally omitted: it breaks cross-RUN package resolution
# because cache-mount contents aren't persisted in image layers between commands.)
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/WatchBack.Api/WatchBack.Api.csproj \
        -c Release \
        -o /app/publish \
        -p:SkipFrontendBuild=true

# ──────────────────────────────────────────
# Stage 3: Runtime image
# ──────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine

WORKDIR /app

# su-exec: lightweight privilege drop (replaces gosu on Alpine, ~20 kB vs ~1.3 MB)
# curl:    healthcheck
# icu-libs: full globalization support (CultureInfo, RequestLocalization, etc.)
#           Alpine's aspnet image ships in invariant mode by default — adding icu-libs
#           and setting DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false re-enables it.
# shadow:  provides groupmod/usermod to rename the built-in 'app' user; removed after use
# hadolint ignore=DL3018
RUN apk add --no-cache su-exec curl icu-libs shadow && \
    groupmod -n watchback app && \
    usermod -l watchback -m -d /home/watchback app && \
    apk del --purge shadow

RUN mkdir -p /app/data

COPY --from=build /app/publish .

COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh && \
    chown -R watchback:watchback /app

EXPOSE 8484

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl --fail http://localhost:8484/health || exit 1

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8484
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

ENTRYPOINT ["docker-entrypoint.sh"]
CMD ["dotnet", "WatchBack.Api.dll"]
