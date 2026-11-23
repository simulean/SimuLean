#!/bin/sh
# Runs test-gate.ps1 with the PowerShell this machine has: Windows PowerShell (powershell.exe) on Windows, else
# PowerShell 7 (pwsh, also on macOS / Linux). Both callers go through it: the Claude Code hook (.claude/settings.json,
# shell form: Git Bash on Windows, sh elsewhere) and the git pre-commit hook (.githooks/pre-commit).
# Without any PowerShell the gate is skipped (exit 0): the Claude Code hook says so once per clone (a systemMessage),
# the pre-commit hook on every commit.
#   sh .claude/hooks/test-gate.sh post|precommit|treefp
mode="${1:-post}"
gate="${0%.sh}.ps1"   # the script beside this one, in the form the caller gave (a Windows path under Git Bash)

for ps in powershell.exe powershell pwsh; do
    if command -v "$ps" >/dev/null 2>&1; then
        exec "$ps" -NoProfile -ExecutionPolicy Bypass -File "$gate" "$mode"
    fi
done

case "$mode" in
    precommit)
        echo "test-gate: no PowerShell found (install PowerShell 7, 'pwsh'): the pre-commit tests are skipped." >&2 ;;
    post)
        state="$(dirname "$0")/.state"
        if [ ! -f "$state/no-powershell-noted" ]; then
            mkdir -p "$state" 2>/dev/null && : > "$state/no-powershell-noted" 2>/dev/null
            echo '{"systemMessage": "test-gate: no PowerShell found (install PowerShell 7, pwsh): the test gate after edits is skipped."}'
        fi ;;
esac
exit 0
