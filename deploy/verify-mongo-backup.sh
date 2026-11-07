#!/usr/bin/env bash
# Restore a backup into a separate temporary volume, never over live data.
set -euo pipefail

readonly ready_attempts=30
readonly ready_interval_seconds=2
readonly memory_limit=640m
readonly wired_tiger_cache_gb=0.25

cd "$(dirname "$0")"
backup=$(realpath "${1:?Usage: verify-mongo-backup.sh backups/TIMESTAMP [--expect-check-record]}")
case "$backup" in
  "$PWD"/backups/*)
    ;;
  *)
    echo 'Backup must be under the deployment backups directory.' >&2
    exit 1
    ;;
esac

(
  cd "$backup"
  sha256sum -c SHA256SUMS
)

set -a
source .env
export MONGO_INITDB_ROOT_PASSWORD="$MONGO_ADMIN_PASSWORD"
export EXPECT_CHECK_RECORD=0
if [ "${2:-}" = --expect-check-record ]; then
  EXPECT_CHECK_RECORD=1
fi
set +a

container=tgame-mongo-restore-check
volume=tgame_restore_check
if docker container inspect "$container" >/dev/null 2>&1 || docker volume inspect "$volume" >/dev/null 2>&1; then
  echo 'Restore-check container or volume already exists; refusing to replace it.' >&2
  exit 1
fi

docker volume create "$volume" >/dev/null
trap '
  docker rm -f "$container" >/dev/null 2>&1 || true
  docker volume rm "$volume" >/dev/null
' EXIT
target=$(docker volume inspect "$volume" --format '{{.Mountpoint}}')
test "$target" = /var/lib/docker/volumes/tgame_restore_check/_data
tar -xzf "$backup/mongo-data.tar.gz" -C "$target"
docker run -d --name "$container" --network none --memory "$memory_limit" \
  -v "$volume:/data/db" -e MONGO_INITDB_ROOT_USERNAME=admin -e MONGO_INITDB_ROOT_PASSWORD -e EXPECT_CHECK_RECORD \
  "$MONGO_IMAGE" mongod --auth --bind_ip 127.0.0.1 --wiredTigerCacheSizeGB "$wired_tiger_cache_gb" >/dev/null

for attempt in $(seq 1 "$ready_attempts"); do
  if [ "$(docker inspect "$container" --format '{{.State.Running}}')" != true ]; then
    echo 'Restore-check MongoDB exited before it became ready.' >&2
    exit 1
  fi

  if docker exec "$container" mongosh --quiet --eval '
    const exitMissingDevUser = 2;
    const exitMissingTestUser = 3;
    const exitMissingCheckRecord = 4;
    const conn = new Mongo("mongodb://admin:" + process.env.MONGO_INITDB_ROOT_PASSWORD + "@127.0.0.1/admin");
    const d = conn.getDB("tgame_dev");

    if (!d.getUser("tgame_dev"))
    {
      quit(exitMissingDevUser);
    }

    if (!conn.getDB("tgame_test").getUser("tgame_test"))
    {
      quit(exitMissingTestUser);
    }

    if (process.env.EXPECT_CHECK_RECORD === "1" && !d.environment_check.findOne({_id:"backup-check",ok:true}))
    {
      quit(exitMissingCheckRecord);
    }

    print("Restored MongoDB users verified; optional backup-check record verified when requested.");
  ' 2>/dev/null; then
    exit 0
  fi

  sleep "$ready_interval_seconds"
done

echo 'Restore verification failed.' >&2
exit 1
