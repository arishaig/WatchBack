# WatchBack

WatchBack is a self-hosted web companion for your media consumption. It connects to Jellyfin or Trakt to see what you are currently watching and fetches real-time reactions from **Bluesky**, **Trakt**, and **Reddit**.

## Features

- **Time Machine Mode**: See comments posted within 14 days of the original air date to avoid spoilers and experience the "live" reaction.
- **Multi-Source**: Fetches discussions from Bluesky, Trakt comments, and Reddit threads.
- **Self-Hosted**: Lightweight FastAPI backend with a simple Alpine.js frontend.
- **Progressive Disclosure**: Works with just a Jellyfin API key, but gets better with Trakt and Bluesky credentials.

## Setup

1. **Install Dependencies**:
   ```bash
   python -m venv .venv
   source .venv/bin/activate
   pip install -r requirements.txt  # Or use uv sync
   ```

2. **Configure Environment**:
   Copy `.env.example` to `.env` and fill in your details:
   ```bash
   cp .env.example .env
   ```

3. **Run the App**:
   - **Locally**:
     ```bash
     uvicorn main:app --reload
     ```
   - **With Docker**:
     ```bash
     docker build -t watchback:dev .
     docker run -d --name watchback -p 8000:8000 --env-file .env watchback:dev
     ```
   Visit `http://localhost:8000` in your browser.

> [!CAUTION]
> **Port Confusion**: If you are using `docker run -p 8000:8000`, you **must** use port **8000**. 
> If you are still seeing errors on port 8080, you are likely hitting an older version of the app!

## Troubleshooting

- **Firefox Connection Errors**: Check that you are using the correct port (8000 vs 8080).
- **Bluesky 403 Forbidden**: Fixed in recent updates by using a browser-like User-Agent.
- **Data is null errors**: Resolved with added optional chaining and null safety guards in the frontend.

| Tier | Required Vars | Feature |
|------|---------------|---------|
| 1 | `JF_API_KEY` | Basic Jellyfin status, Reddit links |
| 2 | + `TRAKT_CLIENT_ID` | Trakt comments enabled |
| 3 | + `BSKY_IDENTIFIER` | Bluesky real-time search enabled |
| 4 | + `TRAKT_USERNAME` | Automated session tracking via Trakt |

## Development

Run tests:
```bash
./.venv/bin/python -m pytest
```
