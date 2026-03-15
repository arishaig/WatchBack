# WatchBackNet Deployment Guide

This guide covers building, configuring, and running WatchBackNet using Docker.

## Prerequisites

- Docker and Docker Compose installed
- Provider credentials (see Configuration section)

## Quick Start

1. **Clone the repository:**
   ```bash
   git clone <repo-url>
   cd WatchBackNet
   ```

2. **Create environment configuration:**
   ```bash
   cp .env.example .env
   # Edit .env with your provider credentials
   ```

3. **Start the application:**
   ```bash
   docker-compose up -d
   ```

4. **Verify the service is running:**
   ```bash
   curl http://localhost:5000/api/sync
   ```

## Configuration

All configuration is managed via environment variables. See `.env.example` for all available options.

### Required Configuration

At minimum, you must configure either Jellyfin or Trakt as your watch state provider:

**For Jellyfin:**
```env
WATCHBACK_PROVIDER=jellyfin
JELLYFIN_BASE_URL=http://jellyfin:8096
JELLYFIN_API_KEY=your_api_key
```

**For Trakt:**
```env
WATCHBACK_PROVIDER=trakt
TRAKT_CLIENT_ID=your_client_id
TRAKT_USERNAME=your_username
TRAKT_ACCESS_TOKEN=your_access_token
```

### Optional Providers

Add one or more thought providers to aggregate reactions:

- **Trakt Comments**: Configure `TRAKT_CLIENT_ID`, `TRAKT_USERNAME`, `TRAKT_ACCESS_TOKEN`
- **Bluesky Posts**: Configure `BLUESKY_HANDLE`, `BLUESKY_APP_PASSWORD` (optional)
- **Reddit Comments**: Automatically enabled (uses public PullPush API, no credentials)

## Docker Compose

The `docker-compose.yml` file defines the WatchBackNet service with:

- **Port Mapping**: 5000:5000 (accessible at http://localhost:5000)
- **Volume Mounting**: `/app/data` - persists SQLite database across container restarts
- **Health Check**: Monitors service health via `/api/sync` endpoint
- **Restart Policy**: Automatically restarts on failure

### Using Existing Services

To integrate with existing Jellyfin or other services:

1. Update `docker-compose.yml` to reference the correct hostname/IP
2. Ensure network connectivity between containers (use `host.docker.internal` if needed)

Example for external Jellyfin:
```env
JELLYFIN_BASE_URL=http://192.168.1.100:8096
```

## Building the Image

To build the Docker image manually:

```bash
docker build -t watchback:latest .
```

The build uses a multi-stage process:
1. **Build Stage**: Uses .NET SDK to compile the application
2. **Runtime Stage**: Uses .NET runtime (smaller image size)

## Database

WatchBackNet uses SQLite for persistence:

- **Location**: `/app/data/watchback.db` (inside container)
- **Persistence**: Mounted to `watchback-data` Docker volume
- **Initialization**: Database schema is automatically created on first run
- **Backups**: Volume can be backed up via Docker volume commands

### Backing Up the Database

```bash
# Create a backup
docker run --rm -v watchback_watchback-data:/data -v $(pwd):/backup \
  alpine tar czf /backup/watchback-backup.tar.gz -C /data watchback.db

# Restore from backup
docker run --rm -v watchback_watchback-data:/data -v $(pwd):/backup \
  alpine tar xzf /backup/watchback-backup.tar.gz -C /data
```

## Monitoring and Logs

### View logs:
```bash
docker-compose logs -f watchback
```

### Health check status:
```bash
docker ps
# or
docker inspect --format='{{json .State.Health}}' watchback-api
```

## Performance Tuning

### Caching

Each provider has configurable cache TTLs (in seconds):
- **Jellyfin**: 10s (near real-time)
- **Trakt**: 30s
- **Reddit**: 86400s (1 day)
- **Bluesky**: 3600s (1 hour)

Adjust in `.env` if needed for your use case.

### Time Machine Window

The "Time Machine" feature filters thoughts to those posted within N days of the episode air date:

```env
WATCHBACK_TIME_MACHINE_DAYS=14  # Default: 14 days
```

## Production Deployment

### Security Considerations

1. **Credentials**: Use Docker secrets or a secrets management system instead of .env files
2. **Network**: Run behind a reverse proxy (nginx, traefik) with HTTPS
3. **Updates**: Regularly pull and rebuild the image for security patches

### Example: Secrets Management

```yaml
# docker-compose.yml with Docker secrets
services:
  watchback:
    secrets:
      - jellyfin_api_key
    environment:
      JELLYFIN_API_KEY_FILE: /run/secrets/jellyfin_api_key

secrets:
  jellyfin_api_key:
    file: ./secrets/jellyfin_api_key.txt
```

### Example: Reverse Proxy (Traefik)

```yaml
services:
  watchback:
    labels:
      - "traefik.enable=true"
      - "traefik.http.routers.watchback.rule=Host(`watchback.example.com`)"
      - "traefik.http.routers.watchback.entrypoints=websecure"
      - "traefik.http.services.watchback.loadbalancer.server.port=5000"
```

## Troubleshooting

### Container won't start
```bash
docker-compose logs watchback
# Check for configuration errors or missing credentials
```

### API returns 500 errors
1. Check application logs: `docker-compose logs watchback`
2. Verify provider configurations are correct
3. Test provider connectivity manually

### Database permission errors
```bash
docker-compose down
docker volume rm watchback_watchback-data  # WARNING: deletes data
docker-compose up -d
```

### Can't reach Jellyfin/external services
- Verify network connectivity
- Use `docker-compose exec watchback curl http://jellyfin:8096/` to test from container
- Check firewalls and port mappings

## Development Builds

For local development without Docker:

```bash
# Restore dependencies
dotnet restore

# Run tests
dotnet test

# Run API
dotnet run --project src/WatchBack.Api
```

Database will be created in your system AppData directory automatically.

## Updates and Upgrades

To update to the latest version:

```bash
git pull origin main
docker-compose down
docker-compose build --no-cache
docker-compose up -d
```

The database schema is migrated automatically on startup.
