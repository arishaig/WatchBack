import { defineConfig } from 'vite';
import tailwindcss from '@tailwindcss/vite';

export default defineConfig({
    root: 'frontend',
    plugins: [tailwindcss()],
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
