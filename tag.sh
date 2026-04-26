#!/usr/bin/env bash
set -euo pipefail

# Find the latest v0.1.x tag and compute the next one
latest=$(git tag -l 'v0.1.*' | sort -V | tail -1)
if [ -z "$latest" ]; then
  next="v0.1.0"
else
  patch=${latest##*0.1.}
  next="v0.1.$((patch + 1))"
fi
version=${next#v}

echo "Latest tag: ${latest:-none}"
echo "Next tag:   $next ($version)"
echo ""

# Update package references
sed -i "s/\"version\": \"0\.1\.[0-9]*\"/\"version\": \"$version\"/" login-app/package.json
sed -i "s|\"@authagonal/login\": \"0\.1\.[0-9]*\"|\"@authagonal/login\": \"$version\"|" demos/custom-server/login-app/package.json
sed -i "s/Version=\"0\.1\.[0-9]*\"/Version=\"$version\"/g" demos/custom-server/CustomAuthServer.csproj

echo "Updated package references:"
grep '"version"' login-app/package.json
grep '@authagonal/login' demos/custom-server/login-app/package.json
grep 'Version=' demos/custom-server/CustomAuthServer.csproj
echo ""

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
