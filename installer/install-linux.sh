#!/usr/bin/env bash
# AegisVault installer for Linux (PKG-1).
#
# Installs the published single-file executable into ~/.local and registers a
# desktop entry. Everything is per-user (no sudo): the same model as the
# Windows per-user installer. Vaults and preferences are never touched.
set -euo pipefail

APP_NAME="AegisVault"
BIN_DIR="${HOME}/.local/bin"
DATA_DIR="${HOME}/.local/share/aegisvault"
DESKTOP_DIR="${XDG_DATA_HOME:-${HOME}/.local/share}/applications"
SOURCE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ ! -f "${SOURCE_DIR}/AegisVault.App" ]]; then
  echo "AegisVault.App not found next to this script; run it from the unpacked folder." >&2
  exit 1
fi

mkdir -p "${BIN_DIR}" "${DATA_DIR}" "${DESKTOP_DIR}"
install -m 0755 "${SOURCE_DIR}/AegisVault.App" "${DATA_DIR}/AegisVault.App"

# Managed libraries ship beside the executable and must stay together.
for file in "${SOURCE_DIR}"/*.so "${SOURCE_DIR}"/*.so.*; do
  [[ -e "${file}" ]] || continue
  install -m 0644 "${file}" "${DATA_DIR}/$(basename "${file}")"
done

ln -sf "${DATA_DIR}/AegisVault.App" "${BIN_DIR}/aegisvault"

cat > "${DESKTOP_DIR}/aegisvault.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=${APP_NAME}
Exec=${BIN_DIR}/aegisvault %U
Icon=${DATA_DIR}/app.png
Terminal=false
Categories=Utility;Security;
EOF

echo "Installed ${APP_NAME}."
echo "  binary:  ${BIN_DIR}/aegisvault"
echo "  desktop: ${DESKTOP_DIR}/aegisvault.desktop"
echo "Uninstall with: rm -rf '${DATA_DIR}' '${BIN_DIR}/aegisvault' '${DESKTOP_DIR}/aegisvault.desktop'"
echo "Your vaults and preferences were not touched."
