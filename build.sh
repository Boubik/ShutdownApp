#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
SOLUTION_PATH="$SCRIPT_DIR/IdleShutdown.sln"
SERVICE_PROJECT="$SCRIPT_DIR/src/IdleShutdown.Service/IdleShutdown.Service.csproj"
AGENT_PROJECT="$SCRIPT_DIR/src/IdleShutdown.Agent/IdleShutdown.Agent.csproj"
PACKAGE_PROJECT="$SCRIPT_DIR/packaging/idle-shutdown/IdleShutdown.Package.csproj"
DIST_DIR="$SCRIPT_DIR/dist"
SERVICE_OUTPUT="$DIST_DIR/Service"
AGENT_OUTPUT="$DIST_DIR/Agent"
DOTNET_COMMAND="${IDLE_SHUTDOWN_DOTNET:-dotnet}"

if [[ ! -f "$SOLUTION_PATH" ]]; then
    echo "[ERROR] IdleShutdown.sln nebyl nalezen v: $SCRIPT_DIR" >&2
    exit 1
fi

if ! command -v "$DOTNET_COMMAND" >/dev/null 2>&1; then
    echo "[ERROR] .NET SDK není nainstalované nebo příkaz dotnet není v PATH." >&2
    echo "        Nainstalujte .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0" >&2
    exit 1
fi

echo "[INFO] Používám .NET SDK: $("$DOTNET_COMMAND" --version)"
echo "[INFO] Čistím předchozí aplikační build..."
rm -rf -- "$SERVICE_OUTPUT" "$AGENT_OUTPUT"
mkdir -p -- "$SERVICE_OUTPUT" "$AGENT_OUTPUT"

echo "[INFO] Publikuji Windows službu (win-x64)..."
"$DOTNET_COMMAND" publish "$SERVICE_PROJECT" \
    -c Release \
    -r win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$SERVICE_OUTPUT"

echo "[INFO] Publikuji uživatelského agenta (win-x64)..."
"$DOTNET_COMMAND" publish "$AGENT_PROJECT" \
    -c Release \
    -r win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$AGENT_OUTPUT"

cp -- "$SCRIPT_DIR/config.test.json" "$DIST_DIR/config.json"

if [[ ! -f "$SERVICE_OUTPUT/IdleShutdown.Service.exe" ||
      ! -f "$AGENT_OUTPUT/IdleShutdown.Agent.exe" ]]; then
    echo "[ERROR] Build skončil bez očekávaných EXE souborů." >&2
    exit 1
fi

echo "[INFO] Vytvářím Chocolatey balíček..."
"$DOTNET_COMMAND" pack "$PACKAGE_PROJECT" \
    -c Release \
    --disable-build-servers

if [[ ! -f "$DIST_DIR/package/idle-shutdown.nupkg" ]]; then
    echo "[ERROR] Chybí dist/package/idle-shutdown.nupkg." >&2
    exit 1
fi

echo
echo "[OK] Build byl úspěšně dokončen."
echo "[OK] Výstup: $DIST_DIR"
echo "[OK] Chocolatey: $DIST_DIR/package/idle-shutdown.nupkg"
