#!/bin/sh
# Runs after the package is installed/upgraded (rpm and deb).
set -e

# Create the unprivileged service account if it doesn't exist.
if ! id peppermill >/dev/null 2>&1; then
    useradd --system --no-create-home --shell /usr/sbin/nologin peppermill \
        || adduser --system --no-create-home --shell /usr/sbin/nologin peppermill \
        || true
fi

systemctl daemon-reload || true
# Enable (start on boot) but do NOT start — the operator must set the master key first.
systemctl enable peppermill.service || true

echo "PepperMill installed."
echo "  1. Set the master key:  sudoedit /etc/peppermill/peppermill.env   (PepperMill__StorageKeyBase64)"
echo "     Generate one with:    head -c 32 /dev/urandom | base64"
echo "  2. systemctl start peppermill   &&   curl http://127.0.0.1:5130/health"
