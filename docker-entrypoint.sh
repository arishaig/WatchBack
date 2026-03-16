#!/bin/sh
# Fix ownership of the bind-mounted data directory if needed.
# This runs as root; after fixing perms we drop to the watchback user.
chown -R watchback:watchback /app/data
exec gosu watchback "$@"
