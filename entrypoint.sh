#!/bin/sh
set -e

PUID=${PUID:-1000}
PGID=${PGID:-1000}

# Adjust abc group/user to match requested IDs
if [ "$(id -g abc)" != "$PGID" ]; then
    groupmod -o -g "$PGID" abc
fi
if [ "$(id -u abc)" != "$PUID" ]; then
    usermod -o -u "$PUID" abc
fi

chown abc:abc /config

exec gosu abc "$@"
