# --------------------------------
# Stage 1: Tailwind CSS build
# --------------------------------
FROM debian:bookworm-slim AS tailwind

RUN apt-get update && \
    apt-get install -y --no-install-recommends curl ca-certificates && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Copy only the Tailwind input so Docker caches properly
COPY src/WatchBack.Api/wwwroot/tw.css /app/wwwroot/tw.css

# Download and install Tailwind (hard-coded version)
RUN curl -sLo /usr/local/bin/tailwindcss \
        https://github.com/tailwindlabs/tailwindcss/releases/download/v4.2.1/tailwindcss-linux-x64 && \
    chmod +x /usr/local/bin/tailwindcss

# Compile CSS (rebuilds only if tw.css changes)
RUN /usr/local/bin/tailwindcss -i /app/wwwroot/tw.css -o /app/wwwroot/tailwind.css --minify

# --------------------------------
# Stage 2: .NET build
# --------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Copy project files first
COPY *.sln .

COPY src/WatchBack.Api/*.csproj src/WatchBack.Api/
COPY src/WatchBack.Core/*.csproj src/WatchBack.Core/
COPY src/WatchBack.Infrastructure/*.csproj src/WatchBack.Infrastructure/

# Restore dependencies
RUN dotnet restore src/WatchBack.Api/*.csproj \
    && dotnet restore src/WatchBack.Core/*.csproj \
    && dotnet restore src/WatchBack.Infrastructure/*.csproj

# Copy the rest of the source files
COPY src/WatchBack.Api/ src/WatchBack.Api/
COPY src/WatchBack.Core/ src/WatchBack.Core/
COPY src/WatchBack.Infrastructure/ src/WatchBack.Infrastructure/

# Copy Tailwind CSS
COPY --from=tailwind /app/wwwroot/tailwind.css src/WatchBack.Api/wwwroot/tailwind.css

# Restore dependencies and publish project
RUN dotnet publish src/WatchBack.Api/WatchBack.Api.csproj \
    -c Release \
    -o /app/publish

# --------------------------------
# Stage 3: Runtime image
# --------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app

RUN apt-get update && \
    apt-get install -y --no-install-recommends curl gosu && \
    rm -rf /var/lib/apt/lists/*

# Create persistent directory
RUN mkdir -p /app/data

# Copy build output
COPY --from=build /app/publish .

# Entrypoint
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh

# Rename default runtime user to watchback
RUN groupmod -n watchback app && \
    usermod -l watchback app && \
    usermod -d /home/watchback -m watchback && \
    chown -R watchback:watchback /app

EXPOSE 8484

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD curl --fail http://localhost:8484/health || exit 1

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8484

ENTRYPOINT ["docker-entrypoint.sh"]
CMD ["dotnet", "WatchBack.Api.dll"]