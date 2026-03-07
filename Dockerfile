# Stage 1: Build & Compile
FROM python:3.14-slim-bookworm AS builder

# Install uv and build tools (curl for Tailwind download)
COPY --from=ghcr.io/astral-sh/uv:latest /uv /uv /usr/local/bin/
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# 1. Install dependencies directly into the system path
RUN --mount=type=cache,target=/root/.cache/uv \
    --mount=type=bind,source=pyproject.toml,target=pyproject.toml \
    --mount=type=bind,source=uv.lock,target=uv.lock \
    uv export --frozen --no-dev --no-hashes --format requirements-txt > requirements.txt && \
    uv pip install --system -r requirements.txt

# 2. Tailwind compilation
COPY static/ /app/static/
RUN set -e; \
    echo "Downloading tailwindcss..." && \
    curl -sLo /tmp/tailwindcss https://github.com/tailwindlabs/tailwindcss/releases/latest/download/tailwindcss-linux-x64 && \
    test -f /tmp/tailwindcss || (echo "Failed to download tailwindcss" && exit 1) && \
    chmod +x /tmp/tailwindcss && \
    printf '@import "tailwindcss";\n@source "./static/index.html";\n' > /app/tw.css && \
    echo "Compiling tailwindcss..." && \
    /tmp/tailwindcss -i /app/tw.css -o /data/static/tailwind.css --minify || (echo "Tailwind compilation failed" && exit 1) && \
    test -f /data/static/tailwind.css || (echo "tailwind.css not created" && exit 1) && \
    rm /tmp/tailwindcss /app/tw.css && \
    ls -lh /data/static/

# Stage 2: Runtime
FROM python:3.14-slim-bookworm
WORKDIR /app

ENV PUID=1000 \
    PGID=1000 \
    TZ=Etc/UTC \
    CONFIG_DIR="/config" \
    DATA_DIR="/data" \
    PYTHONUNBUFFERED=1

# Setup user and directories
RUN groupadd -g $PGID abc && \
    useradd -u $PUID -g abc -m abc && \
    mkdir -p /config && \
    chown abc:abc /config

# Copy installed packages from builder
COPY --from=builder /usr/local/lib/python3.14/site-packages /usr/local/lib/python3.14/site-packages
COPY --from=builder /usr/local/bin /usr/local/bin

# Copy app code
COPY --chown=abc:abc . .
# Copy compiled CSS and source static files from builder
COPY --from=builder --chown=abc:abc /data/static/tailwind.css /data/static/tailwind.css
COPY --chown=abc:abc static/ /data/static/

USER abc
EXPOSE 8000

# Healthcheck
HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
  CMD python3 -c "import urllib.request; urllib.request.urlopen('http://localhost:8000/api/status')" || exit 1

CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000"]