#!/bin/sh
# Runs before the package is removed. On rpm the arg is "0" for a full uninstall (not an upgrade);
# on deb it is "remove". In both cases, stop and disable the service. The store in
# /var/lib/peppermill and the config in /etc/peppermill are left in place on purpose.
set -e

if [ "$1" = "remove" ] || [ "$1" = "0" ]; then
    systemctl disable --now peppermill.service || true
fi
