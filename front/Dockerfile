# Build Stage
FROM node:18-alpine AS build

WORKDIR /app

COPY package*.json ./
RUN npm install

COPY . .
RUN npm run build

# Production Stage
FROM nginx:stable-alpine

# 파일을 Nginx 기본 경로로 복사합니다.
COPY --from=build /app/dist /usr/share/nginx/html/
COPY nginx.conf /etc/nginx/conf.d/default.conf

# Nginx 포트 오픈
EXPOSE 80

CMD ["nginx", "-g", "daemon off;"]
