# syntax=docker/dockerfile:1
# 관리 SPA를 빌드해 Caddy 이미지에 굽는다. 빌드 컨텍스트는 저장소 루트다(deploy/Caddyfile도 함께 들어간다):
#   docker build -f PortfolioBlog.Web/Dockerfile .
FROM node:24.21.0-alpine AS build
WORKDIR /web
COPY PortfolioBlog.Web/package.json PortfolioBlog.Web/package-lock.json ./
# npm ci: package-lock.json과 글자 그대로 같은 트리만 설치한다(버전은 전부 고정돼 있다).
RUN npm ci
COPY PortfolioBlog.Web/ ./
# 배포되는 의존성에 high 이상 취약점이 있으면 이미지를 만들지 않는다(CI의 web 잡과 같은 기준).
# 급한 재배포를 새 권고가 막을 때만: --build-arg NPM_AUDIT=off (deploy/OPERATIONS.md 참고).
ARG NPM_AUDIT=on
RUN if [ "$NPM_AUDIT" = "on" ]; then npm audit --omit=dev --audit-level=high; fi
RUN npm run build

FROM caddy:2.11.4-alpine AS final
COPY deploy/Caddyfile /etc/caddy/Caddyfile
COPY --from=build /web/dist /srv
# 문법 오류를 배포가 아니라 빌드에서 잡는다. 환경변수 자리는 검사용 값으로 채운다.
RUN DOMAIN=blog.example.test ADMIN_DOMAIN=admin.blog.example.test ADMIN_ALLOWED_CIDRS=192.0.2.0/24 ACME_EMAIL=ops@example.test \
    caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
