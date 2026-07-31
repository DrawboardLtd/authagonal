#!/usr/bin/env node
/**
 * Locale drift guard.
 *
 * i18next falls back to `en` for any key a locale is missing, so a dropped translation is invisible
 * in every way that matters: the app builds, typechecks, lints and renders — it just renders in
 * English for that one string. `oidcErrorGeneric` sat missing from seven of the ten shipped locales
 * for exactly that reason. Nothing in the repository could have told anyone.
 *
 * Four assertions, all of them things a human reviewer will not catch in a 174-key JSON diff:
 *
 *  1. Registration — every locale file on disk is listed in i18n/index.ts and vice versa. That file
 *     is the single source for both i18next resources and every language picker, and hi/af/ar have
 *     already gone missing from the dropdowns once by drifting apart from it.
 *  2. Key parity — each locale's key set equals en's, in both directions. An orphan key is as much
 *     a defect as a missing one: it is either a typo shadowing nothing or a string deleted from en
 *     and left behind everywhere else.
 *  3. Interpolation parity — every {{placeholder}} in an en value appears in the translation.
 *     A dropped {{appName}} does not fail anything at runtime, it just renders a sentence with a
 *     hole in it. (Machine translation drops these routinely.)
 *  4. Non-empty — no locale carries "" for a key, which renders as a blank label.
 *
 * Run: npm run check:i18n
 */
import { readFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const I18N_DIR = join(dirname(fileURLToPath(import.meta.url)), '..', 'src', 'i18n');
const REFERENCE = 'en';

const problems = [];
const fail = (msg) => problems.push(msg);

// --- locale files on disk -----------------------------------------------------------------------
const files = readdirSync(I18N_DIR)
  .filter((f) => f.endsWith('.json'))
  .map((f) => f.replace(/\.json$/, ''))
  .sort();

if (!files.includes(REFERENCE)) {
  console.error(`check-i18n: no ${REFERENCE}.json in ${I18N_DIR}`);
  process.exit(1);
}

// --- locales registered in index.ts -------------------------------------------------------------
// Deliberately a regex rather than a TS import: this script has to run under plain node with no
// build step, and the shape it reads (`{ code: 'xx', label: '…', resource: xx }`) is stable.
const index = readFileSync(join(I18N_DIR, 'index.ts'), 'utf8');
const registered = [...index.matchAll(/\{\s*code:\s*'([^']+)'/g)].map((m) => m[1]).sort();

for (const code of files) {
  if (!registered.includes(code)) {
    fail(`${code}.json exists but is not registered in i18n/index.ts — it will never be loaded, and no language picker will offer it`);
  }
}
for (const code of registered) {
  if (!files.includes(code)) {
    fail(`i18n/index.ts registers '${code}' but there is no ${code}.json`);
  }
}

// --- parity -------------------------------------------------------------------------------------
const load = (code) => JSON.parse(readFileSync(join(I18N_DIR, `${code}.json`), 'utf8'));
const reference = load(REFERENCE);
const referenceKeys = Object.keys(reference);
const placeholders = (value) =>
  typeof value === 'string' ? [...value.matchAll(/\{\{\s*([\w.]+)\s*\}\}/g)].map((m) => m[1]) : [];

for (const code of files) {
  if (code === REFERENCE) continue;
  const locale = load(code);

  const missing = referenceKeys.filter((k) => !(k in locale));
  const orphan = Object.keys(locale).filter((k) => !(k in reference));
  if (missing.length) fail(`${code}: ${missing.length} key(s) missing vs ${REFERENCE} — ${missing.join(', ')}`);
  if (orphan.length) fail(`${code}: ${orphan.length} key(s) not in ${REFERENCE} — ${orphan.join(', ')}`);

  for (const key of referenceKeys) {
    if (!(key in locale)) continue;

    if (typeof locale[key] !== 'string') {
      fail(`${code}: ${key} is ${typeof locale[key]}, expected a string`);
      continue;
    }
    if (locale[key].trim() === '') {
      fail(`${code}: ${key} is empty`);
      continue;
    }

    const wanted = placeholders(reference[key]);
    const got = placeholders(locale[key]);
    const dropped = wanted.filter((p) => !got.includes(p));
    const invented = got.filter((p) => !wanted.includes(p));
    if (dropped.length) fail(`${code}: ${key} drops interpolation ${dropped.map((p) => `{{${p}}}`).join(', ')}`);
    if (invented.length) fail(`${code}: ${key} introduces interpolation ${invented.map((p) => `{{${p}}}`).join(', ')} that ${REFERENCE} does not have`);
  }
}

if (problems.length) {
  console.error(`check-i18n: ${problems.length} problem(s)\n`);
  for (const p of problems) console.error(`  ${p}`);
  console.error(`\nEvery locale must carry the same key set as ${REFERENCE}.json. i18next silently falls back to ${REFERENCE}, so a missing key ships as untranslated English rather than as an error.`);
  process.exit(1);
}

console.log(`check-i18n: ${files.length} locales, ${referenceKeys.length} keys each — no drift.`);
