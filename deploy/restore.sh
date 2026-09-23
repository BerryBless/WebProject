#!/usr/bin/env bash
# backup.sh가 만든 디렉터리에서 DB와 첨부를 되돌린다. 지금 있는 DB 내용과 첨부 파일을 전부 지우고 덮어쓴다.
# 사용법: ./restore.sh --yes <백업 디렉터리>
# 새 서버(빈 볼륨)에서도 그대로 쓴다: postgres 초기화 스크립트가 롤과 빈 DB를 만들고, 이 스크립트가 내용을 채운다.
set -euo pipefail
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")"

if [ "${1:-}" != "--yes" ] || [ -z "${2:-}" ]; then
  echo "사용법: $0 --yes <백업 디렉터리>   (현재 DB와 첨부를 전부 덮어쓴다)" >&2
  exit 2
fi
src="$(cd "$2" && pwd)"
(cd "$src" && sha256sum -c SHA256SUMS)

# 쓰는 쪽을 먼저 멈춘다. 없는 서비스를 멈추는 것은 오류가 아니다.
docker compose stop caddy api
docker compose up -d --wait postgres
docker compose exec -T postgres pg_restore -U postgres -d blog --clean --if-exists --single-transaction < "$src/blog.dump"

# api 컨테이너를 "만들기만" 한다: 빈 attachments 볼륨은 이때 이미지의 /data/attachments(소유자 1654, 0700)로 초기화된다.
# 이 단계를 건너뛰면 tools(1654)가 root 소유의 새 볼륨에 쓰지 못한다.
docker compose up --no-start api
docker compose --profile tools run --rm -T --no-deps tools 'find /data/attachments -mindepth 1 -delete && tar -C /data/attachments -xf -' < "$src/attachments.tar"

docker compose up -d --wait
echo "복원 완료: $src"
