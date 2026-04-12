import { cpSync, rmSync } from 'node:fs';
import { resolve } from 'node:path';
import { defineConfig, type Plugin } from 'vite';
import tailwindcss from '@tailwindcss/vite';

// Copies the ionicons distribution into wwwroot so it can be served from the
// same origin (required by our CSP, which does not allow third-party script or
// connect sources). Runs once per build, before Rollup writes its own output.
function copyIonicons(): Plugin {
    return {
        name: 'watchback-copy-ionicons',
        apply: 'build',
        buildStart() {
            const src = resolve(__dirname, 'node_modules/ionicons/dist/ionicons');
            const dest = resolve(__dirname, 'src/WatchBack.Api/wwwroot/ionicons');
            rmSync(dest, { recursive: true, force: true });
            cpSync(src, dest, { recursive: true });
        },
    };
}

export default defineConfig({
    root: 'frontend',
    plugins: [tailwindcss(), copyIonicons()],
    build: {
        outDir: '../src/WatchBack.Api/wwwroot',
        emptyOutDir: false,
        lib: {
            entry: 'src/main.ts',
            formats: ['iife'],
            name: 'WatchBackApp',
        },
        rollupOptions: {
            output: {
                entryFileNames: 'js/app.js',
                assetFileNames: (assetInfo) =>
                    assetInfo.name?.endsWith('.css')
                        ? 'css/app.bundle[extname]'
                        : '[name][extname]',
            },
        },
        minify: true,
        sourcemap: false,
    },
});