import { afterEach } from 'vitest';
import { cleanup } from '@testing-library/react';

/**
 * Unmount between tests. Testing Library registers this itself only when vitest runs with
 * `globals: true`, which this config does not — so without it every render stacks up in the same
 * `document` and a test asserting that something is ABSENT finds the previous test's copy of it.
 */
afterEach(cleanup);

/**
 * jsdom does not implement `window.matchMedia`, and `useDarkMode` calls it on mount to read
 * `prefers-color-scheme` — so any component test that renders the layout throws before it asserts
 * anything. Every real browser has it, so this is an environment gap rather than something the
 * component should be guarding against.
 *
 * Reports "light" and accepts listeners, which is enough for the theme hook to settle. A test that
 * cares about dark mode can override the return value.
 */
if (!window.matchMedia) {
  window.matchMedia = (query: string): MediaQueryList => ({
    matches: false,
    media: query,
    onchange: null,
    addEventListener: () => {},
    removeEventListener: () => {},
    addListener: () => {},
    removeListener: () => {},
    dispatchEvent: () => false,
  }) as MediaQueryList;
}
