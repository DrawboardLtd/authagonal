#!/usr/bin/env bash
set -euo pipefail

# Find the highest vMAJOR.MINOR.PATCH tag and bump the patch. (Was pinned to v0.3.x,
# which silently stopped tracking the line once releases moved to 0.4.x.)
latest=$(git tag -l 'v*' | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | sort -V | tail -1)
if [ -z "$latest" ]; then
  next="v0.0.1"
else
  ver=${latest#v}
  major=${ver%%.*}; rest=${ver#*.}; minor=${rest%%.*}; patch=${rest##*.}
  next="v${major}.${minor}.$((patch + 1))"
fi
version=${next#v}

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

# Commit and tag
git add -A
diff_input=$(printf '%s\n\n%s' "$(git diff --cached --stat)" "$(git diff --cached | head -500)")
unset CLAUDECODE 2>/dev/null || true
msg=$(echo "$diff_input" | claude --model claude-haiku-4-5-20251001 -p \
  'Generate a concise git commit message for these changes. Output only the commit message text with no markdown, code blocks, or explanation. Then output a completely blank line. Then output a hyphen delimited list of the high-level changes. Focus on what behaviour changed or what problem was solved, not on which files, methods, or variables were modified. One point per line.' \
  2>&1 | grep -v '^```' | sed '/^Co-authored-by:/d')
[ -z "$msg" ] && msg="Bump version to $version"
git commit -m "$msg"
echo "Committed with message:"
echo "$msg"
git tag "$next"
echo ""
echo "Tagged $next — run 'git push origin master $next' to publish"
