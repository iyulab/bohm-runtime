#!/usr/bin/env bash
#
# Verifies that the author of a pull request has a CLA signature on record.
#
# The signature is a file the contributor adds in their own pull request
# (.github/cla/<version>/<login>.md), so the ref this workflow checks out already contains it —
# which is why this needs no bot, no external service, no personal access token, and no write
# permission. Inputs arrive through the environment rather than being read out of the event
# payload here, so the same script can be exercised locally.
#
# Note on what this does and does not enforce: for a pull request from a fork, GitHub runs the
# workflow as it exists on the pull request's own branch, so this check is advisory against a
# contributor who edits it away. That is visible in the diff, and merging is a human decision —
# the alternative (pull_request_target) would hand fork code a write-scoped token, which is a
# worse trade for a licensing gate.
set -euo pipefail

login=${LOGIN:?LOGIN is required}
association=${ASSOCIATION:-NONE}
user_type=${USER_TYPE:-User}
version=${CLA_VERSION:-v1}

# Bots (dependency updaters and the like) cannot agree to anything. Requiring a signature from
# them would leave their pull requests permanently red.
if [ "$user_type" = "Bot" ]; then
  echo "Author is a bot ($login) — no CLA required."
  exit 0
fi

# Organization members contribute under iyulab's own copyright, so there is no separate grant to
# make. This encodes an assumption about how membership and copyright line up; it is stated in
# CLA.md so contributors can see the rule they are being measured against.
case "$association" in
  OWNER | MEMBER)
    echo "Author $login is an organization member ($association) — no CLA required."
    exit 0
    ;;
esac

# GitHub logins are case-insensitive, the runner's filesystem is not: normalize before looking.
slug=$(printf '%s' "$login" | tr '[:upper:]' '[:lower:]')
signature=".github/cla/${version}/${slug}.md"

if [ -f "$signature" ]; then
  echo "CLA signature on record for $login ($signature)."
  exit 0
fi

cat >&2 <<MESSAGE
No CLA signature on record for $login.

bohm-runtime is dual-licensed (AGPL-3.0 + commercial), so a contribution can only be merged once the
rights to distribute it under both sets of terms have been granted. Signing takes one file:

  1. Read CLA.md.
  2. Add $signature to this pull request, using the template in CLA.md under "Signing".

Signing once covers your later contributions while version ${version} of the agreement is current.
MESSAGE
exit 1
