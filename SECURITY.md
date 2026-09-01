# Security Policy

## Supported Versions

WatchBack is a self-hosted application under active development. Security fixes
are applied to the `main` branch and included in the next tagged release. Only
the latest release is supported.

| Version | Supported |
|---|---|
| Latest release / `main` | :white_check_mark: |
| Older releases | :x: |

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub issues.**

Instead, use GitHub's private vulnerability reporting:

1. Go to the [Security tab](https://github.com/arishaig/WatchBack/security/advisories/new).
2. Click **Report a vulnerability**.
3. Provide a description of the issue, the affected component, steps to
   reproduce, and the potential impact.

You should receive an acknowledgement within 7 days. If the report is accepted,
we will work with you on a fix and coordinate a disclosure timeline. If it is
declined, we will explain why.

### What to include

- Affected version or commit
- Component (e.g. auth, a specific provider, the config UI)
- A proof of concept or reproduction steps
- Any relevant configuration (with secrets redacted)

## Scope

In scope:

- The WatchBack application code in this repository
- The published container image
- Authentication and forward-auth handling
- Handling of user-supplied configuration and provider credentials

Out of scope:

- Vulnerabilities in third-party services WatchBack talks to (Trakt, Reddit,
  Bluesky, Jellyfin, OMDb, PullPush)
- Issues that require a pre-compromised host or malicious local user
- Denial of service caused by pointing WatchBack at an untrusted upstream

## Hardening notes

WatchBack is intended to run on a private network or behind a trusted reverse
proxy. See [`DEPLOYMENT.md`](DEPLOYMENT.md) for recommended deployment settings,
including forward auth and TLS termination.
