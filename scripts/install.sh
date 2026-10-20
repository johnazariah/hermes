#!/bin/bash
set -euo pipefail
printf '%s\n' \
  'A supported macOS installer has not been promoted.' \
  'Use the self-contained Service/browser preview described in README.md.' \
  'This command does not install, stop, delete or change configuration/archive data.' >&2
exit 3
