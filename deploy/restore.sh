#!/usr/bin/env bash
# backup.sh가 만든 디렉터리에서 DB와 첨부를 되돌린다. 지금 있는 DB 내용과 첨부 파일을 전부 지우고 덮어쓴다.
# 사용법: ./restore.sh --yes <백업 디렉터리>
# 새 서버(빈 볼륨)에서도 그대로 쓴다: postgres 초기화 스크립트가 롤과 빈 DB를 만들고, 이 스크립트가 내용을 채운다.
set -euo pipefail
export MSYS_NO_PATHCONV=1
caller_pwd="$PWD" # cd 전에 호출자의 cwd를 잡아 둔다 — 뒤에서 상대경로 인자를 이 기준으로 푼다
cd "$(dirname "$0")"

if [ "${1:-}" != "--yes" ] || [ -z "${2:-}" ]; then
  echo "사용법: $0 --yes <백업 디렉터리>   (현재 DB와 첨부를 전부 덮어쓴다)" >&2
  exit 2
fi

touched=0 # docker를 실제로 건드리기 전까지는 0 — fail()이 상황에 맞는 메시지를 고른다
fail() {
  if [ "$touched" = 1 ]; then
    echo "복원이 중간에 멈췄다. 서비스는 멈춰 있고 첨부는 비어 있을 수 있다. 같은 백업으로 이 스크립트를 다시 실행하면 처음부터 다시 한다(몇 번을 해도 같다). 계속 실패하면 'docker compose up -d'로 서비스만 올려 이전 상태로 돌아간다." >&2
  else
    echo "복원이 시작되기 전에 멈췄다(백업 디렉터리 확인 실패) — docker는 아직 건드리지 않았다. 서비스는 그대로다. 백업 디렉터리와 SHA256SUMS를 확인한 뒤 다시 시도한다." >&2
  fi
}
trap fail ERR

case "$2" in
  /*) src="$(cd "$2" && pwd)" ;; # 절대경로는 그대로
  *) src="$(cd "$caller_pwd/$2" && pwd)" ;; # 상대경로는 deploy/가 아니라 호출자가 있던 자리 기준으로 푼다
esac
(cd "$src" && sha256sum -c SHA256SUMS)

# 쓰는 쪽을 먼저 멈춘다. 없는 서비스를 멈추는 것은 오류가 아니다.
touched=1
docker compose stop caddy api
docker compose up -d --wait postgres
docker compose exec -T postgres pg_restore -U postgres -d blog --clean --if-exists --single-transaction < "$src/blog.dump"

# api 컨테이너를 "만들기만" 한다: 빈 attachments 볼륨은 이때 이미지의 /data/attachments(소유자 1654, 0700)로 초기화된다.
# 이 단계를 건너뛰면 tools(1654)가 root 소유의 새 볼륨에 쓰지 못한다.
docker compose up --no-start api
docker compose --profile tools run --rm -T --no-deps tools 'find /data/attachments -mindepth 1 -delete && tar -C /data/attachments -xf -' < "$src/attachments.tar"

docker compose up -d --wait
echo "복원 완료: $src"
