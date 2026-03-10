# Build Stage
FROM node:18-alpine AS build

WORKDIR /app

COPY package*.json ./
RUN npm install

COPY . .
RUN npm run build

# Production Stage
FROM nginx:stable-alpine

# 파일을 /vr 서브디렉토리로 복사하여 Nginx 설정과 일치시킵니다.
COPY --from=build /app/dist /usr/share/nginx/html/vr/
COPY nginx.conf /etc/nginx/conf.d/default.conf

# Nginx 포트 오픈
EXPOSE 80

CMD ["nginx", "-g", "daemon off;"]
