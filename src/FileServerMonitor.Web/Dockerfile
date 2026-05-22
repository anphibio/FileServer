FROM node:26-alpine AS build
WORKDIR /app

ARG VITE_API_BASE_URL=http://localhost:8080
ARG VITE_API_KEY=
ARG VITE_ACTOR_NAME=
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL
ENV VITE_API_KEY=$VITE_API_KEY
ENV VITE_ACTOR_NAME=$VITE_ACTOR_NAME

COPY src/FileServerMonitor.Web/package.json ./
RUN npm install

COPY src/FileServerMonitor.Web/ ./
RUN npm run build

FROM nginx:1.27-alpine AS runtime
COPY src/FileServerMonitor.Web/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 80
