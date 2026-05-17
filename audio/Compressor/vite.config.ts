import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Build the Openhpsdr-Zeus Compressor plugin UI module.
//
// Output: `ui/compressor.es.js` — an ESM bundle the host loads at runtime
// after plugin activation. React + react-dom are provided by the host shell
// (via the zeus-sdk shims at /zeus-sdk/react.js + /zeus-sdk/react-jsx-runtime.js
// landed in zeus PR #370), so they're externalised here.
export default defineConfig({
    plugins: [react()],
    build: {
        outDir: 'ui',
        emptyOutDir: false,
        lib: {
            entry: 'ui/compressor.tsx',
            formats: ['es'],
            fileName: () => 'compressor.es.js',
        },
        rollupOptions: {
            external: ['react', 'react-dom'],
        },
        target: 'esnext',
        minify: false,
    },
});
