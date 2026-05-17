import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
    plugins: [react()],
    build: {
        outDir: 'ui',
        emptyOutDir: false,
        lib: {
            entry: 'ui/reverb.tsx',
            formats: ['es'],
            fileName: () => 'reverb.es.js',
        },
        rollupOptions: {
            external: ['react', 'react-dom'],
        },
        target: 'esnext',
        minify: false,
    },
});
