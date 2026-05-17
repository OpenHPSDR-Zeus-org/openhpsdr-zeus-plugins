import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Build the Openhpsdr-Zeus 10-Band EQ plugin UI module.
//
// Output: `ui/eq.es.js` — an ESM bundle the host loads at runtime
// after plugin activation. React + react-dom are provided by the host
// shell (via the zeus-sdk shims at /zeus-sdk/react.js +
// /zeus-sdk/react-jsx-runtime.js landed in zeus PR #370).
export default defineConfig({
    plugins: [react()],
    build: {
        outDir: 'ui',
        emptyOutDir: false,
        lib: {
            entry: 'ui/eq.tsx',
            formats: ['es'],
            fileName: () => 'eq.es.js',
        },
        rollupOptions: {
            external: ['react', 'react-dom'],
        },
        target: 'esnext',
        minify: false,
    },
});
