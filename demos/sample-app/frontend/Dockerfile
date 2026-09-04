# Stage 1: Build the React SPA
FROM node:26-alpine@sha256:aadf416b2cdce311a8811ba3f0608a61b77dbf997500e2eafe781b51f6a0b019 AS build
WORKDIR /app
COPY demos/sample-app/frontend/package*.json ./
RUN npm ci
COPY demos/sample-app/frontend/ ./
RUN npm run build

# Stage 2: Serve with nginx
FROM nginx:alpine@sha256:72ba65eb42c10344912a84ff42408db7d34f2feb642204570ab8fc5ffd29f1d3
RUN apk add --no-cache gettext
COPY --from=build /app/dist /usr/share/nginx/html
RUN rm -rf /etc/nginx/templates
COPY demos/sample-app/frontend/docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh
EXPOSE 8080
ENTRYPOINT ["/docker-entrypoint.sh"]
