import { defineConfig } from 'vitest/config';
import path from 'path';
import { fileURLToPath } from 'url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

/**
 * A test config of its own rather than a `test` block on vite.config.ts, because that config is a LIBRARY
 * build: it externalises react/react-dom/react-router and runs the tailwind plugin, neither of which a unit
 * test wants. Sharing it made the alias the only thing worth inheriting, so the alias is restated here.
 *
 * jsdom because the controls under test are browser controls — `isSameOriginPath` reads
 * `window.location.origin`, and the export guard reads `Response.headers`.
 */
export default defineConfig({
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },
  test: {
    environment: 'jsdom',
    include: ['src/**/*.test.{ts,tsx}'],
    setupFiles: ['./vitest.setup.ts'],
    restoreMocks: true,
  },
});
