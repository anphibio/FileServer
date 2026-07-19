FROM node:26-alpine AS build
WORKDIR /app

ARG VITE_API_BASE_URL=http://localhost:8080
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL

COPY src/FileServerMonitor.Web/package.json src/FileServerMonitor.Web/package-lock.json ./
RUN npm ci

COPY src/FileServerMonitor.Web/ ./
RUN npm run build

FROM nginx:1.27-alpine AS runtime
COPY src/FileServerMonitor.Web/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 80
