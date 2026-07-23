#!/usr/bin/env bash
set -euo pipefail

# An explicit "MAJOR.MINOR.PATCH" (or "vMAJOR.MINOR.PATCH") argument overrides the default patch bump —
# needed for the minor/major releases the auto-bump can't produce. Otherwise: find the highest
# vMAJOR.MINOR.PATCH tag and bump the patch. (Was pinned to v0.3.x, which silently stopped tracking the
# line once releases moved to 0.4.x.)
if [ -n "${1:-}" ]; then
  version="${1#v}"
  if ! printf '%s' "$version" | grep -qE '^[0-9]+\.[0-9]+\.[0-9]+$'; then
    echo "ERROR: version '$1' is not MAJOR.MINOR.PATCH"; exit 1
  fi
  next="v$version"
else
  latest=$(git tag -l 'v*' | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | sort -V | tail -1)
  if [ -z "$latest" ]; then
    next="v0.0.1"
  else
    ver=${latest#v}
    major=${ver%%.*}; rest=${ver#*.}; minor=${rest%%.*}; patch=${rest##*.}
    next="v${major}.${minor}.$((patch + 1))"
  fi
  version=${next#v}
fi

echo "Latest tag: ${latest:-none}"
echo "Next tag:   $next ($version)"
echo ""

# Update the npm package version (CI derives the publish version from the tag,
# but the in-repo version should not drift). The demos float on 0.x / "0.x"
# references, so they no longer need per-release bumps.
sed -i "s/\"version\": \"0\.[0-9]*\.[0-9]*\"/\"version\": \"$version\"/" login-app/package.json

echo "Updated package references:"
grep '"version"' login-app/package.json
echo ""

# The CHANGELOG rotted from 0.4.0 to 0.7.8 because tags were minted without it —
# refuse to tag until the release is written up (content under [Unreleased] or a
# [$version] entry).
unreleased_lines=$(sed -n '/^## \[Unreleased\]/,/^## \[/p' CHANGELOG.md | sed '1d;$d' | grep -c -v '^[[:space:]]*$' || true)
if [ "$unreleased_lines" -eq 0 ] && ! grep -q "\[$version\]" CHANGELOG.md; then
  echo "ERROR: CHANGELOG.md has an empty [Unreleased] section and no [$version] entry."
  echo "Write the changelog entry for $next, then re-run."
  exit 1
fi

# Tests are a tagging precondition. v0.10.24–v0.10.27 shipped with a non-compiling test project
# because nothing built the tests at tag time — build the whole solution, run the suite, and
# typecheck the login app before minting anything. set -e aborts the tag if any step fails.
echo "Building, testing, and typechecking before tag…"
dotnet build src/src.sln -c Release --nologo
dotnet test tests/Authagonal.Tests/Authagonal.Tests.csproj -c Release --nologo
( cd login-app && npm run build )
echo ""

# Commit and tag. Stage ONLY the release-metadata files this script owns — feature/code changes
# must be committed separately first. `git add -A` used to sweep stray working-tree files into the
# release commit, and the haiku-generated message occasionally captured error output (the garbage
# messages on v0.1.33/34); a templated message is deterministic.
other=$(git status --porcelain --untracked-files=no | grep -vE '(CHANGELOG\.md|login-app/package\.json)$' || true)
if [ -n "$other" ]; then
  echo "WARNING: uncommitted changes OUTSIDE the release metadata — they will NOT be in $next."
  echo "Commit your feature work first if it belongs in this release:"
  echo "$other"
  echo ""
fi

if git diff --quiet -- CHANGELOG.md login-app/package.json; then
  echo "No CHANGELOG.md / login-app/package.json changes to commit — tagging current HEAD as $next."
else
  git add CHANGELOG.md login-app/package.json
  git commit -m "chore(release): $next"
  echo "Committed release metadata for $next."
fi
git tag "$next"
echo ""
echo "Tagged $next — run 'git push origin master $next' to publish"
