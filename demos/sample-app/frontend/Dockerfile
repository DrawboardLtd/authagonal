# Stage 1: Build the React SPA
FROM node:26-alpine@sha256:233761595746769ebfdb6090f44fc7cdf818ae0ce62d2b37e0367723b9823e36 AS build
WORKDIR /app
COPY demos/sample-app/frontend/package*.json ./
RUN npm ci
COPY demos/sample-app/frontend/ ./
RUN npm run build

# Stage 2: Serve with nginx
FROM nginx:alpine@sha256:4a73073bd557c65b759505da037898b61f1be6cbcc3c2c3aeac22d2a470c1752
RUN apk add --no-cache gettext
COPY --from=build /app/dist /usr/share/nginx/html
RUN rm -rf /etc/nginx/templates
COPY demos/sample-app/frontend/docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh
EXPOSE 8080
ENTRYPOINT ["/docker-entrypoint.sh"]
