/**
 * Lightweight Reddit-flavored markdown → HTML renderer.
 */
function renderMarkdown(src) {
    if (!src) return '';

    // Fenced code blocks (```...```)
    let t = src.replace(/```([\s\S]*?)```/g, (_, code) =>
        '<pre class="wb-md-codeblock">' + code.trim() + '</pre>');

    // Process line-based features
    const lines = t.split('\n');
    const out = [];
    let inQuote = false;
    for (let i = 0; i < lines.length; i++) {
        let line = lines[i];
        const quoteMatch = line.match(/^&gt;(?!!)\s?(.*)/);
        if (quoteMatch) {
            if (!inQuote) { out.push('<blockquote class="wb-md-quote">'); inQuote = true; }
            out.push(quoteMatch[1]);
        } else {
            if (inQuote) { out.push('</blockquote>'); inQuote = false; }
            // Headings
            const hMatch = line.match(/^(#{1,4})\s+(.*)/);
            if (hMatch) {
                const level = Math.min(hMatch[1].length + 2, 6);
                out.push(`<h${level} class="wb-md-heading">${hMatch[2]}</h${level}>`);
            } else if (line.match(/^\s*[-*]\s+/)) {
                out.push('<li class="wb-md-li">' + line.replace(/^\s*[-*]\s+/, '') + '</li>');
            } else {
                out.push(line);
            }
        }
    }
    if (inQuote) out.push('</blockquote>');
    t = out.join('\n');

    // Wrap list items
    t = t.replace(/((?:<li class="wb-md-li">.*<\/li>\n?)+)/g,
        (m) => '<ul class="wb-md-ul">' + m + '</ul>');

    // Inline formatting
    t = t.replace(/`([^`\n]+)`/g, '<code class="wb-md-code">$1</code>');
    t = t.replace(/\*\*\*(.+?)\*\*\*/g, '<strong><em>$1</em></strong>');
    t = t.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
    t = t.replace(/(?<!\w)\*(.+?)\*(?!\w)/g, '<em>$1</em>');
    t = t.replace(/~~(.+?)~~/g, '<del>$1</del>');
    t = t.replace(/\^(\([^)]+\)|\S+)/g, (_, content) =>
        '<sup>' + content.replace(/^\(|\)$/g, '') + '</sup>');
    t = t.replace(/&gt;!(.+?)!&lt;/g,
        '<span class="wb-md-spoiler" tabindex="0" role="button" aria-label="Reveal spoiler" onclick="this.classList.add(\'revealed\')" onkeydown="if(event.key===\'Enter\')this.classList.add(\'revealed\')">$1</span>');
    t = t.replace(/\[([^\]]+)\]\((https?:\/\/[^)]+)\)/g,
        '<a href="$2" target="_blank" rel="noopener noreferrer" class="wb-md-link">$1</a>');
    t = t.replace(/(?<!="|'>)(https?:\/\/[^\s<)]+)/g,
        '<a href="$1" target="_blank" rel="noopener noreferrer" class="wb-md-link">$1</a>');
    t = t.replace(/^(-{3,}|\*{3,})$/gm, '<hr class="wb-md-hr">');

    return t;
}

document.addEventListener('alpine:init', () => {
    Alpine.data('app', () => ({
        // App state
        data: null,
        error: null,
        errorTimer: null,
        isLoading: false,
        lightboxImg: null,
        showConfig: false,

        // Config state
        watchStateStatus: { name: '', healthy: false },
        thoughtProviders: [
            { name: 'Trakt', healthy: false },
            { name: 'Reddit', healthy: false },
            { name: 'Bluesky', healthy: false }
        ],
        timeMachineDays: 14,

        async init() {
            console.log("[WatchBack] Initializing application");
            this.applyTheme();
            this.loadConfig();
            await this.sync();
            this.setupSSE();
        },

        loadConfig() {
            // Derive config from current data
            if (!this.data) return;

            // Determine watch state provider from data
            this.watchStateStatus = {
                name: this.data.status === 'Watching' ? 'Jellyfin/Trakt' : 'Unknown',
                healthy: this.data.status !== 'Error'
            };

            // Mark providers as healthy if they have results
            if (this.data.sourceResults) {
                const sourceNames = this.data.sourceResults.map(s => s.source);
                this.thoughtProviders = this.thoughtProviders.map(p => ({
                    ...p,
                    healthy: sourceNames.includes(p.name)
                }));
            }

            // Set time machine days from data
            if (this.data.timeMachineDays) {
                this.timeMachineDays = this.data.timeMachineDays;
            }
        },

        setupSSE() {
            const es = new ReconnectingEventSource('/api/sync/stream', { max_retry_time: 60000 });
            es.onmessage = (e) => {
                if (e.data) {
                    try {
                        const data = JSON.parse(e.data.replace(/^data: /, ''));
                        console.debug("[WatchBack] SSE update:", data);
                        this.data = data;
                    } catch (err) {
                        console.debug("[WatchBack] SSE parse:", err);
                    }
                }
            };
            es.onerror = () => {
                console.warn("[WatchBack] SSE connection lost");
            };
        },

        applyTheme(mode) {
            const themeMode = mode || 'dark';
            document.documentElement.setAttribute('data-theme', themeMode);
        },

        showError(msg) {
            this.error = msg;
            clearTimeout(this.errorTimer);
            this.errorTimer = setTimeout(() => { this.error = null; }, 8000);
        },

        async sync() {
            if (this.isLoading) return;
            console.debug("[WatchBack] Syncing data...");
            this.isLoading = true;
            try {
                const res = await fetch('/api/sync?t=' + Date.now());
                const newData = await res.json();

                if (newData?.status === 'Watching') {
                    this.error = null;
                    console.info(`[WatchBack] Synced: ${newData.title}`, {
                        sources: newData.sourceResults?.length || 0,
                        thoughts: newData.allThoughts?.length || 0
                    });
                } else if (newData?.status === 'Idle') {
                    console.debug("[WatchBack] No active session");
                    this.data = newData;
                } else {
                    console.warn(`[WatchBack] Sync status: ${newData?.status}`);
                }

                this.data = newData;
                this.loadConfig();
            } catch (e) {
                console.error("[WatchBack] Sync failed:", e);
                this.showError('Connection failed');
            }
            finally { this.isLoading = false; }
        },

        formatDate(iso) {
            if (!iso) return '';
            try {
                return new Date(iso).toLocaleDateString();
            } catch {
                return iso;
            }
        },

        formatScore(n) {
            if (n == null) return '';
            if (n >= 1000) return (n / 1000).toFixed(1).replace(/\.0$/, '') + 'k';
            return String(n);
        }
    }))
})
