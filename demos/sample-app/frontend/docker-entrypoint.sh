#!/bin/sh
set -e

# Generate runtime config from environment variables
cat > /usr/share/nginx/html/config.json <<EOF
{
  "AUTH_SERVER": "${AUTH_SERVER:-http://localhost:8080}",
  "REDIRECT_URI": "${REDIRECT_URI:-http://localhost:3000/callback}",
  "CLIENT_ID": "${CLIENT_ID:-sample-app}"
}
EOF

# Substitute API_BASE into nginx config
API_BASE="${API_BASE:-http://localhost:5001}"
envsubst '$API_BASE' < /etc/nginx/templates/default.conf.template > /etc/nginx/conf.d/default.conf

exec nginx -g 'daemon off;'
