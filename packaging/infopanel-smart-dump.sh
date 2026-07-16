#!/usr/bin/env bash
# Dumps SMART data for all drives to /run/infopanel/smart.json (world-readable)
# so InfoPanel's SMART plugin can expose drive health without root.
set -u
OUT_DIR=/run/infopanel
OUT=$OUT_DIR/smart.json
mkdir -p "$OUT_DIR"

first=1
{
  echo "["
  for dev in /dev/nvme[0-9] /dev/sd[a-z]; do
    [ -e "$dev" ] || continue
    data=$(smartctl -j -i -A -H "$dev" 2>/dev/null) || continue
    [ -n "$data" ] || continue
    [ $first -eq 0 ] && echo ","
    first=0
    printf '%s' "$data"
  done
  echo
  echo "]"
} > "$OUT.tmp"
chmod 0644 "$OUT.tmp"
mv "$OUT.tmp" "$OUT"
