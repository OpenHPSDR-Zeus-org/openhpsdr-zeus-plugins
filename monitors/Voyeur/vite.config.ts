import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Build the Voyeur Mode · Net Monitor plugin UI module.
//
// Output: `ui/voyeur-panel.es.js` — an ESM bundle the host loads at runtime
// after plugin activation. react + react-dom are provided by the host shell
// (externals). voyeur.css is imported ?raw by the entry and injected as a
// <style> at load. Mirrors amplifiers/Rf2k and audio/Eq harnesses.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: 'ui',
    emptyOutDir: false,
    lib: {
      entry: 'ui/voyeur-panel.tsx',
      formats: ['es'],
      fileName: () => 'voyeur-panel.es.js',
    },
    rollupOptions: {
      external: ['react', 'react-dom'],
    },
    target: 'esnext',
    minify: false,
  },
});
