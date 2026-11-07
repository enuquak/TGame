#!/usr/bin/env bash
# Consistent development-environment backup. Briefly stops only this Compose project.
set -euo pipefail

readonly compose_wait_timeout_seconds=120

cd "$(dirname "$0")"
umask 077
exec 9>.backup.lock
if ! flock -n 9; then
  echo 'Another backup is running.' >&2
  exit 1
fi

stamp=$(TZ=Asia/Shanghai date +%Y%m%d-%H%M%S)
destination="$PWD/backups/$stamp"
mkdir -p "$destination"

trap 'docker compose up -d --pull never >/dev/null' EXIT
docker compose stop
tar -czf "$destination/config.tar.gz" compose.yaml .env secrets config images.lock.json

while IFS= read -r volume; do
  mountpoint=$(docker volume inspect "tgame_$volume" --format '{{.Mountpoint}}')
  case "$mountpoint" in
    /var/lib/docker/volumes/tgame_*/_data)
      ;;
    *)
      echo 'Unexpected volume path' >&2
      exit 1
      ;;
  esac

  tar -czf "$destination/$volume.tar.gz" -C "$mountpoint" .
done < <(docker compose config --volumes)

(
  cd "$destination"
  sha256sum ./*.tar.gz > SHA256SUMS
  sha256sum -c SHA256SUMS
)

docker compose up -d --wait --wait-timeout "$compose_wait_timeout_seconds" --pull never
trap - EXIT
printf 'Backup complete: %s\n' "$destination"
