import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { resolve } from 'node:path';

export default defineConfig({
  plugins: [react()],
  define: {
    'process.env.NODE_ENV': JSON.stringify('production'),
    'process.env': '{}'
  },
  build: {
    outDir: resolve(__dirname, '../wwwroot/workflow-editor'),
    emptyOutDir: true,
    sourcemap: false,
    lib: {
      entry: resolve(__dirname, 'src/main.jsx'),
      name: 'BibiWorkflowEditorBundle',
      formats: ['iife'],
      fileName: () => 'workflow-editor.js',
      cssFileName: 'workflow-editor'
    }
  }
});
