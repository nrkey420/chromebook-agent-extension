#!/usr/bin/env bash
# Installs go-sqlcmd (the SQL command-line tool the SQL scripts use) into ~/bin. No admin rights needed, so it works in
# Azure Cloud Shell, where your home folder is kept between sessions.
# Optional env vars: GO_SQLCMD_VERSION (default v1.10.0, same as the deploy workflow), SQLCMD_DIR (default ~/bin).
set -euo pipefail
VERSION=${GO_SQLCMD_VERSION:-v1.10.0}
DEST=${SQLCMD_DIR:-$HOME/bin}
case "$(uname -m)" in
  x86_64 | amd64) ARCH=amd64 ;;
  aarch64 | arm64) ARCH=arm64 ;;
  *) echo "Unsupported CPU architecture: $(uname -m)" >&2; exit 1 ;;
esac
mkdir -p "$DEST"
curl -sSL "https://github.com/microsoft/go-sqlcmd/releases/download/${VERSION}/sqlcmd-linux-${ARCH}.tar.bz2" | tar xj -C "$DEST" sqlcmd
"$DEST/sqlcmd" --version | head -3
case ":$PATH:" in
  *":$DEST:"*) ;;
  *) echo
     echo "Installed to $DEST, which is not on your PATH. Run:"
     echo "  export PATH=\"$DEST:\$PATH\""
     echo "and add that line to ~/.bashrc to keep it for new sessions." ;;
esac
