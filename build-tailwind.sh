#!/usr/bin/env bash
# Build Tailwind CSS from the index.html source.
# Requires the tailwindcss standalone CLI: https://tailwindcss.com/blog/standalone-cli
#
# Install:
#   curl -sLo ~/.local/bin/tailwindcss https://github.com/tailwindlabs/tailwindcss/releases/latest/download/tailwindcss-linux-x64
#   chmod +x ~/.local/bin/tailwindcss
#
# Usage:
#   ./build-tailwind.sh          # one-shot build
#   ./build-tailwind.sh --watch  # watch mode for development

set -euo pipefail

WWWROOT="$(dirname "$0")/src/WatchBack.Api/wwwroot"

if ! command -v tailwindcss &>/dev/null; then
    echo "tailwindcss CLI not found. Install it first (see comments in this script)." >&2
    exit 1
fi

tailwindcss -i "$WWWROOT/tw.css" -o "$WWWROOT/tailwind.css" --minify "$@"
echo "Tailwind CSS compiled → $WWWROOT/tailwind.css"
