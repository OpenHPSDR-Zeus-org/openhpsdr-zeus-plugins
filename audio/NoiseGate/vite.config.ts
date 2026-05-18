import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Build the Openhpsdr-Zeus Noise Gate plugin UI module.
//
// Output: `ui/noisegate.es.js` — an ESM bundle the host loads at runtime
// after plugin activation. React + react-dom are provided by the host shell
// so they're externalised here.
export default defineConfig({
    plugins: [react()],
    build: {
        outDir: 'ui',
        emptyOutDir: false,
        lib: {
            entry: 'ui/noisegate.tsx',
            formats: ['es'],
            fileName: () => 'noisegate.es.js',
        },
        rollupOptions: {
            external: ['react', 'react-dom'],
        },
        target: 'esnext',
        minify: false,
    },
});
