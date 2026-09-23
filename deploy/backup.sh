#!/usr/bin/env bash
# DB 덤프와 첨부 파일을 한 디렉터리에 백업한다. 서비스는 멈추지 않는다.
# 사용법: ./backup.sh [백업 루트(기본 ./backups)]  → 만든 디렉터리 경로를 마지막 줄에 출력한다.
#
# 순서가 곧 일관성이다: DB를 먼저 덤프하고 파일을 나중에 묶는다. 첨부는 내용 주소 파일이라 덮어써지지 않으므로,
# 그 사이에 올라온 파일은 "행 없는 파일"(복원 뒤 청소 잡이 지운다)이 될 뿐이고 "파일 없는 행"은 그 사이에 첨부를 지웠을 때만 생긴다.
# dpkeys(세션 암호화 키)와 caddy_data(인증서)는 백업하지 않는다 — 복원하면 다시 로그인하고 인증서는 다시 발급된다.
set -euo pipefail
export MSYS_NO_PATHCONV=1 # Windows Git Bash에서 /data 같은 컨테이너 경로가 바뀌지 않게 한다(Linux에서는 영향 없음)
cd "$(dirname "$0")"
umask 077

root="${1:-backups}"
dest="$root/$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$dest"

docker compose exec -T postgres pg_dump -U postgres -Fc blog > "$dest/blog.dump"
docker compose --profile tools run --rm -T --no-deps tools 'tar -C /data/attachments --exclude=./.tmp -cf - .' > "$dest/attachments.tar"

# 읽을 수 있는 백업인지 그 자리에서 확인한다(빈 파일·잘린 파일을 백업이라고 믿지 않는다).
docker compose exec -T postgres pg_restore -l < "$dest/blog.dump" > /dev/null
tar -tf "$dest/attachments.tar" > /dev/null
(cd "$dest" && sha256sum blog.dump attachments.tar > SHA256SUMS)

echo "$dest"
