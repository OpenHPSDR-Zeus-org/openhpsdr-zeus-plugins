import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Build the Openhpsdr-Zeus RX Recorder plugin UI module.
//
// Output: `ui/recorder.es.js` — an ESM bundle the host loads at runtime after
// plugin activation. React + react-dom are provided by the host shell (via the
// zeus-sdk shims), so they're externalised here.
export default defineConfig({
    plugins: [react()],
    build: {
        outDir: 'ui',
        emptyOutDir: false,
        lib: {
            entry: 'ui/recorder.tsx',
            formats: ['es'],
            fileName: () => 'recorder.es.js',
        },
        rollupOptions: {
            external: ['react', 'react-dom'],
        },
        target: 'esnext',
        minify: false,
    },
});
