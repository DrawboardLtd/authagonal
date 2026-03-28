# Stage 1: Build the React SPA
FROM node:22-alpine AS build
WORKDIR /app
COPY demos/sample-app/frontend/package*.json ./
RUN npm ci
COPY demos/sample-app/frontend/ ./
RUN npm run build

# Stage 2: Serve with nginx
FROM nginx:alpine
RUN apk add --no-cache gettext
COPY --from=build /app/dist /usr/share/nginx/html
RUN rm -rf /etc/nginx/templates
COPY demos/sample-app/frontend/docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh
EXPOSE 8080
ENTRYPOINT ["/docker-entrypoint.sh"]
