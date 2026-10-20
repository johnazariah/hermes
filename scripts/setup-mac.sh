#!/bin/bash
set -euo pipefail
printf '%s\n' \
  'The legacy automatic macOS/MAUI installer is excluded from support.' \
  'Build the canonical Service/browser preview with the commands in README.md.' \
  'This command does not install workloads, change launchd, remove applications or alter user data.' >&2
exit 3
