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

API_BASE="${API_BASE:-http://localhost:5001}"

# Write nginx config with API_BASE substituted
cat > /etc/nginx/conf.d/default.conf <<NGINX
server {
    listen 8080;
    root /usr/share/nginx/html;
    index index.html;

    location /api/ {
        proxy_pass ${API_BASE}/api/;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
    }

    location / {
        try_files \$uri \$uri/ /index.html;
    }
}
NGINX

exec nginx -g 'daemon off;'
