#!/usr/bin/env bash
#
# Builds PepperMill as a self-contained single-file binary (no .NET runtime needed on the server)
# and generates a ready-to-deploy systemd bundle in deploy/out/ + a tarball.
#
# Usage:   bash deploy/publish.sh            # linux-x64, Release
#          RID=linux-arm64 bash deploy/publish.sh
#
# Then copy the tarball to the server and run the installer:
#          tar xzf peppermill-<rid>.tar.gz && sudo ./install.sh
#
set -euo pipefail

RID="${RID:-linux-x64}"
CONFIG="${CONFIG:-Release}"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/deploy/out"
BIN="FactFoundry.PepperMill"

echo "==> Publishing self-contained single-file ($CONFIG, $RID)…"
rm -rf "$OUT"
mkdir -p "$OUT/app"
dotnet publish "$ROOT/src/FactFoundry.PepperMill" \
    -c "$CONFIG" -r "$RID" --self-contained \
    -p:PublishSingleFile=true \
    -o "$OUT/app"

# Trim files a production server doesn't need.
rm -f "$OUT/app/"*.pdb "$OUT/app/appsettings.Development.json"

echo "==> Adding systemd unit + env template (from deploy/)…"
cp "$ROOT/deploy/peppermill.service" "$OUT/peppermill.service"
cp "$ROOT/deploy/peppermill.env.example" "$OUT/peppermill.env.example"

echo "==> Generating installer…"
cat > "$OUT/install.sh" <<'EOF'
#!/usr/bin/env bash
# Run on the target server as root:  sudo ./install.sh
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "Run as root (sudo)."; exit 1; }
cd "$(dirname "$0")"

id peppermill &>/dev/null || useradd --system --no-create-home --shell /usr/sbin/nologin peppermill

install -d -o root -g root /opt/peppermill
cp -rf app/. /opt/peppermill/
chmod +x /opt/peppermill/FactFoundry.PepperMill

install -d -o root -g root /etc/peppermill
NEED_KEY=0
if [ ! -f /etc/peppermill/peppermill.env ]; then
    install -m 600 -o root -g root peppermill.env.example /etc/peppermill/peppermill.env
    NEED_KEY=1
fi

install -m 644 peppermill.service /etc/systemd/system/peppermill.service
systemctl daemon-reload
systemctl enable peppermill

echo
echo "Installed. Next:"
[ "$NEED_KEY" = 1 ] && echo "  1. Set the master key:  sudoedit /etc/peppermill/peppermill.env  (PepperMill__StorageKeyBase64)"
echo "  2. systemctl start peppermill   &&   curl http://127.0.0.1:5130/health"
echo "  Logs:  journalctl -u peppermill -f"
EOF
chmod +x "$OUT/install.sh"

TARBALL="$ROOT/deploy/peppermill-$RID.tar.gz"
tar -czf "$TARBALL" -C "$OUT" app peppermill.service peppermill.env.example install.sh

echo
echo "Done."
echo "  Bundle:  $TARBALL"
echo "  Deploy:  scp it to the server, then  'tar xzf $(basename "$TARBALL") && sudo ./install.sh'"
