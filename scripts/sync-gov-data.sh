#!/usr/bin/env bash
set -euo pipefail

# Skrypt pomocniczy dla serwera Linux (bez Dockera):
# Pobiera bazę pytań (.xlsx) oraz oba archiwa multimediów (.zip) ze strony gov.pl,
# rozpakowuje załączniki bezpośrednio do katalogu data/media i NATYCHMIAST usuwa
# źródłowe archiwa .zip w celu zaoszczędzenia miejsca na dysku.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

echo "==> Uruchamianie pełnej synchronizacji bazy gov.pl przez Drvrs (.NET 11)..."
dotnet run --project "${REPO_ROOT}/src/Drvrs.Web/Drvrs.Web.csproj" -c Release -- --sync-all "$@"
echo "==> Gotowe!"
