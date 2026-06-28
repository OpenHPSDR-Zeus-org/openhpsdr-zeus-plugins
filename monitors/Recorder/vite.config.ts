import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Build the WAV Recorder · Tape Deck plugin UI module.
//
// Output: `ui/wav-recorder-panel.es.js` — an ESM bundle the host loads at
// runtime after plugin activation. react + react-dom are provided by the host
// shell (externals). WavRecorderPanel.css is imported ?raw by the entry and
// injected as a <style> at load. Mirrors monitors/Voyeur + audio/* harnesses.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: 'ui',
    emptyOutDir: false,
    lib: {
      entry: 'ui/wav-recorder-panel.tsx',
      formats: ['es'],
      fileName: () => 'wav-recorder-panel.es.js',
    },
    rollupOptions: {
      external: ['react', 'react-dom'],
    },
    target: 'esnext',
    minify: false,
  },
});
