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
sed -i "s/\"@drawboard\/authagonal-login\": \"0\.1\.[0-9]*\"/\"@drawboard\/authagonal-login\": \"$version\"/" demos/custom-server/login-app/package.json
sed -i "s/Version=\"0\.1\.[0-9]*\"/Version=\"$version\"/g" demos/custom-server/CustomAuthServer.csproj

echo "Updated package references:"
grep '"version"' login-app/package.json
grep 'authagonal-login' demos/custom-server/login-app/package.json
grep 'Version=' demos/custom-server/CustomAuthServer.csproj
echo ""

# Commit and tag
git aicc
git tag "$next"
echo ""
echo "Tagged $next — run 'git push origin master $next' to publish"
