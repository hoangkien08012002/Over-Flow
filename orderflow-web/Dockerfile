# syntax=docker/dockerfile:1
# Build from the repository root so the edge configuration in deploy/ is in the context:
#   docker build -f orderflow-web/Dockerfile -t orderflow-web .
# One Caddy container serves the static client, proxies /api to the API, and terminates TLS.
# Same origin for page and API is what lets the SameSite=Strict refresh cookie work without CORS.

FROM node:22-alpine AS build
WORKDIR /web
COPY orderflow-web/package.json orderflow-web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY orderflow-web/ ./
RUN npm run build

FROM caddy:2.10-alpine AS web
COPY deploy/Caddyfile /etc/caddy/Caddyfile
COPY --from=build /web/dist /srv
