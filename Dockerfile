# Stage 1: Build & Compile
FROM python:3.12-slim-bookworm AS builder

# Install uv
COPY --from=ghcr.io/astral-sh/uv:latest /uv /uv /usr/local/bin/

WORKDIR /app

# 1. Install dependencies directly into the system path
# We use --system to avoid the virtualenv headache inside Docker
RUN --mount=type=cache,target=/root/.cache/uv \
    --mount=type=bind,source=pyproject.toml,target=pyproject.toml \
    --mount=type=bind,source=uv.lock,target=uv.lock \
    uv export --frozen --no-dev --format requirements-txt > requirements.txt && \
    pip install --no-cache-dir -r requirements.txt

# 2. Tailwind compilation
COPY static/ /app/static/
RUN apt-get update && apt-get install -y curl && \
    curl -sLo /tmp/tailwindcss https://github.com/tailwindlabs/tailwindcss/releases/latest/download/tailwindcss-linux-x64 && \
    chmod +x /tmp/tailwindcss && \
    printf '@import "tailwindcss";\n@source "./static/index.html";\n' > /app/tw.css && \
    /tmp/tailwindcss -i /app/tw.css -o /app/static/tailwind.css --minify && \
    rm /tmp/tailwindcss /app/tw.css

# Stage 2: Runtime
FROM python:3.12-slim-bookworm
WORKDIR /app

ENV PUID=1000 \
    PGID=1000 \
    TZ=Etc/UTC \
    CONFIG_DIR="/config" \
    PYTHONUNBUFFERED=1

# Setup user
RUN groupadd -g $PGID abc && \
    useradd -u $PUID -g abc -m abc && \
    mkdir /config && chown abc:abc /config

# Copy installed packages from builder
COPY --from=builder /usr/local/lib/python3.12/site-packages /usr/local/lib/python3.12/site-packages
COPY --from=builder /usr/local/bin /usr/local/bin

# Copy app code
COPY --chown=abc:abc . .
# Overwrite with compiled CSS from builder
COPY --from=builder /app/static/tailwind.css /app/static/tailwind.css

USER abc
EXPOSE 8000

# Healthcheck
HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
  CMD python3 -c "import urllib.request; urllib.request.urlopen('http://localhost:8000/api/status')" || exit 1

CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000"]