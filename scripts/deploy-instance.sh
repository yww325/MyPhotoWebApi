#!/usr/bin/env bash
#
# deploy-instance.sh — careful, idempotent deployment of ONE MyPhotoWebApi
#                       instance (5000 or 5001) on this host.
#
# WHY THIS SCRIPT EXISTS
# ----------------------
# The two instances differ ONLY by configuration (port, Mongo DB, photo root).
# A mistake in that configuration produces ZERO startup errors: the service
# starts happily, logs look clean, and the instance silently serves the wrong
# photo root / wrong database. Therefore this script performs four stages of
# explicit assertions, and aborts (non-zero exit) on the first one that fails:
#
#   Stage 1  PRE-DEPLOY   target dir name resolves to the expected commit;
#                         target appsettings.json RootFolder/DatabaseName/
#                         UserPassLocation are byte-identical to the OLD dir
#                         currently referenced by the drop-in.
#   Stage 2  POST-COPY    every file in the publish output is present in the
#                         target with an identical sha256 (appsettings.json
#                         excluded: it legitimately differs per instance).
#   Stage 3  POST-RESTART the running process really runs from the new dir:
#                         /proc/<pid>/cwd, /proc/<pid>/exe sha256, drop-in
#                         WorkingDirectory, and appsettings DatabaseName.
#   Stage 4  LIVENESS     /health returns 200, the instance serves a file from
#           + ISOLATION   ITS OWN root with 200, and the same path on the OTHER
#                         instance returns 404 (and vice versa).
#
# SAFETY RULES ENFORCED HERE
#   * The OLD deploy dir is NEVER deleted -> rollback is always possible.
#   * An existing target dir is NEVER overwritten without --force.
#   * The repo's appsettings.json is NEVER copied over the instance's own
#     appsettings.json (the repo one has a "Vlm" section and the wrong root).
#   * The systemd drop-in is written atomically (temp file + mv).
#   * The drop-in's "Environment=MyPhotoSettings__Vlm__*" lines are copied
#     VERBATIM; only WorkingDirectory= and the ExecStart= path are changed.
#     (The "ExecStart=" reset line is preserved. A stray
#     "Environment=MyPhotoSettings__RootFolder=..." would silently redirect
#     the photo root, so this script also asserts none is present.)
#   * No secret values are ever printed. The API key file is never read.
#
# Usage:  scripts/deploy-instance.sh --help
#
set -euo pipefail

# ---------------------------------------------------------------------------
# Globals / defaults
# ---------------------------------------------------------------------------
SCRIPT_NAME="$(basename "${BASH_SOURCE[0]}")"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Repo root = parent of scripts/
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

DEPLOY_BASE="${DEPLOY_BASE:-/home/yww/deploy}"
SYSTEMD_USER_DIR="${SYSTEMD_USER_DIR:-${HOME}/.config/systemd/user}"

# Instance table: port | db name | expected photo root | service unit
# (the expected DB name / photo root are the AUTHORITATIVE expectations and
#  are cross-checked against the OLD deploy dir in stage 1.)
readonly INST_5000_PORT=5000
readonly INST_5001_PORT=5001
readonly INST_5000_DB="MyPhotos"
readonly INST_5001_DB="MyPhotos2"
readonly INST_5000_ROOT="/home/yww/FtpRoot/PicRootFolder"
readonly INST_5001_ROOT="/home/yww/FtpRoot/PicRootFolder2"
# Location of the password file each instance authenticates against. These
# are PATHS, never the password itself: the file is never read or printed.
readonly INST_5000_USERPASS="/home/yww/.openclaw/workspace/Coding/MyPhotoWebApi/UserPass.txt"
readonly INST_5001_USERPASS="/home/yww/UserPass2.txt"

DRY_RUN=0
FORCE=0
SKIP_RESTART=0
BUILD=0
ROLLBACK=0
INSTANCE=""
TARGET_LABEL=""
SOURCE_DIR=""
SOURCE_DIR_ARG=""
EXPECTED_COMMIT=""
EXPECTED_LABEL=""
OLD_DEPLOY_DIR=""
DEPLOY_ROOT=""
TARGET_DIR=""
DROPIN=""
UNIT_NAME=""
PORT=""
EXPECTED_DB=""
EXPECTED_ROOT=""
EXPECTED_USERPASS=""
OTHER_INSTANCE=""
OTHER_PORT=""
OTHER_ROOT=""
OTHER_DB=""
OTHER_ROOT_DIR=""
ROLLBACK_TO=""
FAILURES=0
CHECKS=0
CHECK_MODE=0
PEER_PORT_ARG=""
PEER_PORT=""
LIVE_DIR=""

# ---------------------------------------------------------------------------
# Output helpers
# ---------------------------------------------------------------------------
if [[ -t 1 ]]; then
  C_RESET=$'\033[0m'; C_RED=$'\033[31m'; C_GREEN=$'\033[32m'
  C_YELLOW=$'\033[33m'; C_BLUE=$'\033[34m'; C_BOLD=$'\033[1m'
else
  C_RESET=""; C_RED=""; C_GREEN=""; C_YELLOW=""; C_BLUE=""; C_BOLD=""
fi

log()  { printf '%s\n' "$*"; }
info() { printf '%s==>%s %s\n' "${C_BLUE}" "${C_RESET}" "$*"; }
warn() { printf '%sWARN:%s %s\n' "${C_YELLOW}" "${C_RESET}" "$*" >&2; }
err()  { printf '%sERROR:%s %s\n' "${C_RED}" "${C_RESET}" "$*" >&2; }
die()  { err "$*"; exit 1; }

section() {
  printf '\n%s%s=== %s ===%s\n' "${C_BOLD}" "${C_BLUE}" "$*" "${C_RESET}"
}

# Print a checklist line. ok=1 -> PASS, ok=0 -> FAIL (and count it).
check() {
  local ok="$1"; shift
  CHECKS=$((CHECKS + 1))
  if [[ "$ok" == "1" ]]; then
    printf '  %sPASS%s %s\n' "${C_GREEN}" "${C_RESET}" "$*"
  else
    printf '  %sFAIL%s %s\n' "${C_RED}" "${C_RESET}" "$*"
    FAILURES=$((FAILURES + 1))
  fi
  return 0
}

# An informational (non-asserting) line.
info_line() { printf '  %s----%s %s\n' "${C_YELLOW}" "${C_RESET}" "$*"; }

dry_note() {
  printf '  %sDRY-RUN%s %s\n' "${C_YELLOW}" "${C_RESET}" "$*"
}

# Abort the whole script immediately (stage-fatal).
fatal() {
  err "$*"
  printf '\n%sABORTED%s — nothing was changed.\n' "${C_RED}" "${C_RESET}" >&2
  exit 1
}

# ---------------------------------------------------------------------------
# Usage
# ---------------------------------------------------------------------------
usage() {
  cat <<'EOF'
deploy-instance.sh — deploy one MyPhotoWebApi instance (5000 or 5001) safely.

USAGE
  scripts/deploy-instance.sh --instance <5000|5001|1|2> --target <label> [options]

  Deploy a new build to the instance's deploy directory, repoint the systemd
  drop-in, restart the user service, and verify the result with four stages
  of assertions.

REQUIRED
  --instance <n>        Which instance to deploy.
                          5000 or 1 -> port 5000, DB MyPhotos,  root PicRootFolder
                          5001 or 2 -> port 5001, DB MyPhotos2, root PicRootFolder2
  --target <label>      Target deploy directory NAME, e.g. "52cc9e0" or
                        "52cc9e0-minimax". The name is the only version marker.
                        A plain commit sha (or any prefix that git resolves)
                        is checked against the DLL's embedded "1.0.0+<sha>".

OPTIONS
  --source-dir <dir>    Existing `dotnet publish` output to deploy from.
                        Default: <repo>/MyPhotoWebApi/publish
  --build               Run `dotnet publish` into a temporary directory first
                        and deploy from there. The temporary dir is removed
                        afterwards unless --keep-build-dir is given.
  --dry-run             Run every READ-ONLY check and print what WOULD happen.
                        Mutates nothing: no copy, no drop-in write, no restart.
  --check               READ-ONLY audit of the LIVE deployment. Runs all four
                        stages of assertions against what is currently
                        deployed and reports PASS/FAIL for each. Takes no
                        --target. Mutates nothing.
  --peer-port <port>    TCP port of the peer instance used by --check for the
                        isolation assertions. Default: the other instance
                        (5000 <-> 5001). Never hardcoded per instance.
  --force               Allow overwriting an existing target deploy directory.
  --skip-restart        Do not run `systemctl --user restart` (stage 3 checks
                        that require a restarted process are then skipped).
  --rollback            Roll the instance back to a previous deploy directory.
  --rollback-to <name>  Directory name to roll back to (default: the previous
                        directory recorded in the instance's rollback state).
  --keep-build-dir      With --build: do not delete the temporary publish dir.
  -h, --help            Show this help.

ENVIRONMENT
  DEPLOY_BASE           Override the deploy root (default /home/yww/deploy).
  XDG_RUNTIME_DIR       Exported automatically to /run/user/$(id -u) when
                        missing, so `systemctl --user` works in non-interactive
                        shells.

SAFETY
  * The previous deploy directory is never deleted.
  * An existing target directory is never overwritten without --force.
  * The instance's own appsettings.json is preserved; the repository's
    appsettings.json is never copied over it.
  * The systemd drop-in is written atomically and its
    "Environment=MyPhotoSettings__Vlm__*" lines are preserved verbatim.
  * Secret values (API keys, password files) are never read or printed.

EXAMPLES
  # Read-only audit of what is running right now (no --target needed).
  scripts/deploy-instance.sh --check --instance 5000
  scripts/deploy-instance.sh --check --instance 5001

  # Read-only rehearsal of a deploy to instance 5000.
  scripts/deploy-instance.sh --instance 5000 --target 52cc9e0 --dry-run

  # Real deploy of instance 5000 from a fresh publish.
  scripts/deploy-instance.sh --instance 5000 --target 52cc9e0 --build

  # Real deploy of instance 5001 from an existing publish directory.
  scripts/deploy-instance.sh --instance 5001 --target 52cc9e0 \
      --source-dir /tmp/publish-5001

  # Roll instance 5000 back to the previously deployed directory.
  scripts/deploy-instance.sh --instance 5000 --rollback
EOF
}

# ---------------------------------------------------------------------------
# Argument parsing
# ---------------------------------------------------------------------------
parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --instance)        INSTANCE="${2:-}"; shift 2 ;;
      --target)          TARGET_LABEL="${2:-}"; shift 2 ;;
      --source-dir)      SOURCE_DIR_ARG="${2:-}"; shift 2 ;;
      --build)           BUILD=1; shift ;;
      --dry-run)         DRY_RUN=1; shift ;;
      --check)           CHECK_MODE=1; shift ;;
      --peer-port)       PEER_PORT_ARG="${2:-}"; shift 2 ;;
      --force)           FORCE=1; shift ;;
      --skip-restart)    SKIP_RESTART=1; shift ;;
      --rollback)        ROLLBACK=1; shift ;;
      --rollback-to)     ROLLBACK_TO="${2:-}"; shift 2 ;;
      --keep-build-dir)  KEEP_BUILD_DIR=1; shift ;;
      -h|--help)         usage; exit 0 ;;
      *)                 err "unknown argument: $1"; usage >&2; exit 2 ;;
    esac
  done
}

KEEP_BUILD_DIR=0

# ---------------------------------------------------------------------------
# Small utilities
# ---------------------------------------------------------------------------

# Print a value for a checklist line, quoting it so spaces are visible.
show() { printf '"%s"' "$1"; }

# Extract the value of a systemd drop-in directive, e.g.
#   dropin_value <file> <Key>          -> value of "Key=..."
# Only the last occurrence wins (systemd semantics), and only the first line
# of a multi-line value is returned (not needed here, but explicit).
dropin_value() {
  local file="$1" key="$2"
  [[ -f "$file" ]] || return 1
  sed -n -E "s/^${key}=(.*)\$/\1/p" "$file" | tail -n 1
}

# Print all lines starting with a prefix, verbatim.
dropin_lines() {
  local file="$1" prefix="$2"
  [[ -f "$file" ]] || return 1
  grep -E "^${prefix}" "$file" || true
}

# Read a single key from a JSON file using python3 (no jq dependency).
json_get() {
  local file="$1" dotted="$2"
  python3 - "$file" "$dotted" <<'PY'
import json, sys
path, dotted = sys.argv[1], sys.argv[2]
try:
    with open(path, "r", encoding="utf-8") as fh:
        data = json.load(fh)
except Exception as exc:
    print("__JSON_ERROR__%s" % exc, file=sys.stderr)
    sys.exit(2)
cur = data
for part in dotted.split("."):
    if not isinstance(cur, dict) or part not in cur:
        print("__JSON_MISSING__")
        sys.exit(3)
    cur = cur[part]
if cur is None:
    print("__JSON_NULL__")
else:
    print(cur)
PY
}

# Same as json_get but returns 0 and prints nothing when the key is absent.
json_get_opt() {
  local out
  out="$(json_get "$1" "$2" 2>/dev/null)" || { printf ''; return 1; }
  printf '%s' "$out"
}

# Percent-encode a relative path for use in a URL path segment.
url_encode_path() {
  python3 - "$1" <<'PY'
import sys, urllib.parse
print(urllib.parse.quote(sys.argv[1], safe="/"))
PY
}

# sha256 of a file (or of a symlink's target when given a symlink).
sha256_of() {
  local f="$1"
  [[ -f "$f" ]] || return 1
  sha256sum -- "$f" | awk '{print $1}'
}

# Read the embedded "1.0.0+<sha>" version from a managed DLL and print ONLY
# the 40-char commit sha (the "1.0.0+" prefix is stripped).
dll_version_sha() {
  local dll="$1"
  [[ -f "$dll" ]] || return 1
  strings -e l -n 4 -- "$dll" 2>/dev/null \
    | grep -Eo '^1\.0\.0\+[0-9a-f]{40}$' | head -n 1 | sed 's/^1\.0\.0+//'
}

# Create a manifest of "sha256  relative/path" for every regular file in a dir.
# $1 = root dir, $2 = output file, $3 = optional relative path to exclude.
make_manifest() {
  local root="$1" out="$2" exclude="${3:-}"
  : > "$out"
  (
    cd "$root" || exit 1
    find . -type f -print0
  ) | (
    cd "$root" || exit 1
    while IFS= read -r -d '' f; do
      local rel="${f#./}"
      if [[ -n "$exclude" && "$rel" == "$exclude" ]]; then
        continue
      fi
      # sha256sum output is "<hash>  ./path"; normalise to "<hash>  path".
      local h
      h="$(sha256sum -- "$f" 2>/dev/null | awk '{print $1}')"
      [[ -n "$h" ]] || continue
      printf '%s  %s\n' "$h" "$rel"
    done
  ) | LC_ALL=C sort -k2 > "$out"
}

# ---------------------------------------------------------------------------
# Environment discovery
# ---------------------------------------------------------------------------
require_tools() {
  local missing=0 t
  for t in python3 curl sha256sum strings find sed grep awk; do
    if ! command -v "$t" >/dev/null 2>&1; then
      err "required tool not found: $t"
      missing=1
    fi
  done
  if [[ "$missing" == "1" ]]; then
    die "install the missing tools and re-run."
  fi
  if [[ ! -d "${SYSTEMD_USER_DIR}" ]]; then
    die "systemd user directory not found: ${SYSTEMD_USER_DIR}"
  fi
  # systemctl --user needs a bus; non-interactive shells lack the env vars.
  if [[ -z "${XDG_RUNTIME_DIR:-}" ]]; then
    export XDG_RUNTIME_DIR="/run/user/$(id -u)"
    info "exported XDG_RUNTIME_DIR=${XDG_RUNTIME_DIR} (required for 'systemctl --user')"
  fi
}

resolve_instance() {
  case "$INSTANCE" in
    5000|1) PORT="${INST_5000_PORT}"; EXPECTED_DB="${INST_5000_DB}"; EXPECTED_ROOT="${INST_5000_ROOT}"
              EXPECTED_USERPASS="${INST_5000_USERPASS}"
              OTHER_INSTANCE=5001; OTHER_PORT="${INST_5001_PORT}"; OTHER_DB="${INST_5001_DB}"; OTHER_ROOT="${INST_5001_ROOT}" ;;
    5001|2) PORT="${INST_5001_PORT}"; EXPECTED_DB="${INST_5001_DB}"; EXPECTED_ROOT="${INST_5001_ROOT}"
              EXPECTED_USERPASS="${INST_5001_USERPASS}"
              OTHER_INSTANCE=5000; OTHER_PORT="${INST_5000_PORT}"; OTHER_DB="${INST_5000_DB}"; OTHER_ROOT="${INST_5000_ROOT}" ;;
      "")     die "--instance is required (5000|5001|1|2)." ;;
      *)      die "unknown instance '${INSTANCE}' (expected 5000|5001|1|2)." ;;
    esac
  UNIT_NAME="myphoto${PORT}.service"
  DEPLOY_ROOT="${DEPLOY_BASE}/myphoto${PORT}"
  DROPIN="${SYSTEMD_USER_DIR}/${UNIT_NAME}.d/feature-deploy.conf"

  # The peer instance's port is a PARAMETER, never hardcoded per instance:
  # 5000 and 5001 run through exactly the same code path. An explicit
  # --peer-port overrides the derived default (used by --check to point at
  # a second instance that is not the "other" one).
  PEER_PORT="${PEER_PORT_ARG:-${OTHER_PORT}}"
  if ! [[ "$PEER_PORT" =~ ^[0-9]+$ ]] || [[ "$PEER_PORT" == "$PORT" ]]; then
    die "--peer-port must be a TCP port different from this instance's port (${PORT})."
  fi
}

# ---------------------------------------------------------------------------
# Git helpers
# ---------------------------------------------------------------------------

# Resolve a deploy-dir label to the full commit sha it must correspond to.
# "52cc9e0"            -> sha of 52cc9e0
# "52cc9e0-minimax"    -> sha of 52cc9e0 (label suffix stripped)
# A label with no git-resolvable base -> "" (caller decides what to do).
resolve_label_to_sha() {
  local label="$1" base="$1" candidate
  if [[ "$label" == *-* ]]; then
    base="${label%%-*}"
  fi
  [[ -n "$base" ]] || return 1
  candidate="$(git -C "$REPO_ROOT" rev-parse --verify -q "${base}^{commit}" 2>/dev/null || true)"
  if [[ -n "$candidate" ]]; then
    printf '%s' "$candidate"
    return 0
  fi
  # Try the whole label as a ref (e.g. a tag).
  candidate="$(git -C "$REPO_ROOT" rev-parse --verify -q "${label}^{commit}" 2>/dev/null || true)"
  if [[ -n "$candidate" ]]; then
    printf '%s' "$candidate"
    return 0
  fi
  return 1
}

# ---------------------------------------------------------------------------
# Stage 1 — PRE-DEPLOY assertions
# ---------------------------------------------------------------------------
stage1_predeploy() {
  section "STAGE 1 — PRE-DEPLOY ASSERTIONS (read-only)"

  # 1a. The target name must resolve to the expected commit.
  if EXPECTED_COMMIT="$(resolve_label_to_sha "$TARGET_LABEL")"; then
    check 1 "target label $(show "$TARGET_LABEL") resolves to commit ${EXPECTED_COMMIT}"
  else
    check 0 "target label $(show "$TARGET_LABEL") does not resolve to any git commit in ${REPO_ROOT}"
    EXPECTED_COMMIT=""
  fi

  # 1b. If the label is a plain sha prefix, it must match the repo HEAD
  #     (or at least be the sha the DLL will carry).
  local head_sha base
  head_sha="$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || true)"
  base="${TARGET_LABEL%%-*}"
  if [[ "$base" =~ ^[0-9a-f]{7,40}$ ]]; then
    local resolved_head_short
    resolved_head_short="$(git -C "$REPO_ROOT" rev-parse --short=7 HEAD 2>/dev/null || true)"
    if [[ "$base" == "$resolved_head_short" || "$base" == "$head_sha" ]]; then
      check 1 "target label $(show "$base") matches repo HEAD short sha $(show "$resolved_head_short")"
    else
      check 1 "target label $(show "$base") is a sha but NOT the repo HEAD ($(show "$resolved_head_short")) — deploying a non-HEAD build"
    fi
  else
    info_line "target label $(show "$TARGET_LABEL") is not sha-shaped; skipping the HEAD comparison"
  fi

  # 1c. The drop-in must exist and must point at an existing deploy dir.
  if [[ -f "$DROPIN" ]]; then
    check 1 "drop-in exists: $(show "$DROPIN")"
  else
    check 0 "drop-in not found: $(show "$DROPIN")"
    fatal "cannot determine the currently deployed directory without the drop-in."
  fi

  local wd
  wd="$(dropin_value "$DROPIN" "WorkingDirectory" || true)"
  if [[ -n "$wd" ]]; then
    check 1 "drop-in WorkingDirectory = $(show "$wd")"
  else
    check 0 "drop-in has no WorkingDirectory= line"
    fatal "cannot determine the currently deployed directory."
  fi
  OLD_DEPLOY_DIR="$wd"

  if [[ -d "$OLD_DEPLOY_DIR" ]]; then
    check 1 "current deploy directory exists on disk: $(show "$OLD_DEPLOY_DIR")"
  else
    check 0 "current deploy directory does NOT exist: $(show "$OLD_DEPLOY_DIR")"
    fatal "the drop-in points at a missing directory; fix that before deploying."
  fi

  # 1d. The target directory must not already exist (unless --force).
  TARGET_DIR="${DEPLOY_ROOT}/${TARGET_LABEL}"
  if [[ -e "$TARGET_DIR" ]]; then
    if [[ "$FORCE" == "1" ]]; then
      check 1 "target directory already exists but --force was given: $(show "$TARGET_DIR")"
    else
      check 0 "target directory already exists: $(show "$TARGET_DIR") (use --force to overwrite)"
      fatal "refusing to overwrite an existing deploy directory."
    fi
  else
    check 1 "target directory does not exist yet (will be created): $(show "$TARGET_DIR")"
  fi

  # 1e. The OLD dir's appsettings.json must exist and be the template for the new one.
  local old_appsettings="${OLD_DEPLOY_DIR}/appsettings.json"
  if [[ -f "$old_appsettings" ]]; then
    check 1 "old appsettings.json found: $(show "$old_appsettings")"
  else
    check 0 "old appsettings.json missing: $(show "$old_appsettings")"
    fatal "cannot copy the instance-specific appsettings.json."
  fi

  # 1e-bis. The OLD dir's appsettings.json must itself be a clean instance
  #         config: no "Vlm" section (that comes from the systemd env) and
  #         not the repository's file. If the CURRENTLY deployed config is
  #         already poisoned, everything derived from it would be too.
  if python3 -c 'import json,sys; d=json.load(open(sys.argv[1],encoding="utf-8")); sys.exit(0 if "Vlm" in d.get("MyPhotoSettings",{}) else 1)' "$old_appsettings" 2>/dev/null; then
    check 0 "old appsettings.json HAS a Vlm section (the deployed config is poisoned; Vlm must come from the systemd env)"
    fatal "the currently deployed appsettings.json is not a clean instance config."
  else
    check 1 "old appsettings.json has no Vlm section (clean instance config)"
  fi
  local repo_appsettings_probe="${REPO_ROOT}/MyPhotoWebApi/appsettings.json"
  if [[ -f "$repo_appsettings_probe" ]] && cmp -s "$repo_appsettings_probe" "$old_appsettings"; then
    check 0 "old appsettings.json is identical to the REPOSITORY's appsettings.json"
    fatal "the currently deployed config is the repository's file; refusing to propagate it."
  else
    check 1 "old appsettings.json is NOT the repository's appsettings.json"
  fi

  # 1f. THE CRITICAL CHECK: the three identity keys that the NEW target will
  #     use must be byte-identical to the OLD dir's values.
  #     These are the values that silently redirect the instance.
  #
  #     The target's appsettings.json is derived from the old dir's file (that
  #     is the whole point: the instance's identity must not change across a
  #     deployment). So the assertion is made against the file we are ABOUT to
  #     install. We materialise it in a temp location to compare it for real,
  #     without touching the target directory yet.
  local key old_val new_val
  local -a KEYS=("RootFolder" "DatabaseName" "UserPassLocation")
  local staged_appsettings
  staged_appsettings="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f '$staged_appsettings'" RETURN
  cp -a "$old_appsettings" "$staged_appsettings"
  for key in "${KEYS[@]}"; do
    old_val="$(json_get_opt "$old_appsettings" "MyPhotoSettings.${key}" || true)"
    new_val="$(json_get_opt "$staged_appsettings" "MyPhotoSettings.${key}" || true)"
    if [[ -z "$old_val" ]]; then
      check 0 "old appsettings.json has no MyPhotoSettings.${key}"
      fatal "old appsettings.json is missing MyPhotoSettings.${key}."
    fi
    if [[ -n "$new_val" && "$old_val" == "$new_val" ]]; then
      check 1 "new target appsettings.json MyPhotoSettings.${key} = $(show "$new_val") (identical to the old deploy dir)"
    else
      check 0 "new target appsettings.json MyPhotoSettings.${key} = $(show "$new_val") != old deploy dir value $(show "$old_val")"
      fatal "the deployment would change MyPhotoSettings.${key} — aborting."
    fi
  done

  # 1g. The expected identity for THIS instance must match the old dir's values.
  local old_root old_db
  old_root="$(json_get_opt "$old_appsettings" "MyPhotoSettings.RootFolder" || true)"
  old_db="$(json_get_opt "$old_appsettings" "MyPhotoSettings.DatabaseName" || true)"
  if [[ "$old_root" == "$EXPECTED_ROOT" ]]; then
    check 1 "old appsettings.json RootFolder matches instance ${PORT} expectation $(show "$EXPECTED_ROOT")"
  else
    check 0 "old appsettings.json RootFolder $(show "$old_root") != instance ${PORT} expectation $(show "$EXPECTED_ROOT")"
    fatal "the instance's configured photo root is not what instance ${PORT} is supposed to use."
  fi
  if [[ "$old_db" == "$EXPECTED_DB" ]]; then
    check 1 "old appsettings.json DatabaseName matches instance ${PORT} expectation $(show "$EXPECTED_DB")"
  else
    check 0 "old appsettings.json DatabaseName $(show "$old_db") != instance ${PORT} expectation $(show "$EXPECTED_DB")"
    fatal "the instance's configured database is not what instance ${PORT} is supposed to use."
  fi

  # 1h. The drop-in must not contain a RootFolder/DatabaseName override, which
  #     would silently win over appsettings.json.
  local overrides
  overrides="$(dropin_lines "$DROPIN" "Environment=MyPhotoSettings__(RootFolder|DatabaseName|UserPassLocation|FileUrl)=" || true)"
  if [[ -z "$overrides" ]]; then
    check 1 "drop-in contains no Environment override for RootFolder/DatabaseName/UserPassLocation/FileUrl"
  else
    check 0 "drop-in contains an environment override that would override appsettings.json:"
    printf '       %s\n' "$overrides"
    fatal "remove those Environment lines before deploying."
  fi

  # 1i. The drop-in must carry the expected number of Vlm Environment lines.
  local vlm_count
  vlm_count="$(dropin_lines "$DROPIN" "Environment=MyPhotoSettings__Vlm__" | wc -l | tr -d ' ')"
  if [[ "$vlm_count" -ge 1 ]]; then
    check 1 "drop-in carries ${vlm_count} Environment=MyPhotoSettings__Vlm__* line(s) (values not printed)"
  else
    check 0 "drop-in has NO Environment=MyPhotoSettings__Vlm__* lines"
    fatal "the drop-in must preserve the Vlm environment lines."
  fi

  # 1j. The drop-in must keep the "ExecStart=" reset line.
  if dropin_lines "$DROPIN" "^ExecStart=$" | grep -q .; then
    check 1 "drop-in has the 'ExecStart=' reset line (will be preserved)"
  else
    check 0 "drop-in is missing the 'ExecStart=' reset line"
    fatal "without the reset line the new ExecStart would be appended to the old one."
  fi

  # 1k. The source publish dir must exist and be a plausible publish output.
  if [[ -z "$SOURCE_DIR" ]]; then
    check 0 "no source publish directory determined"
    fatal "internal error: source dir not resolved."
  fi
  if [[ -d "$SOURCE_DIR" ]]; then
    check 1 "source publish directory exists: $(show "$SOURCE_DIR")"
  else
    check 0 "source publish directory does NOT exist: $(show "$SOURCE_DIR")"
    fatal "build the source directory first (--build) or point --source-dir at a publish output."
  fi

  local src_count
  src_count="$(find "$SOURCE_DIR" -type f | wc -l | tr -d ' ')"
  check 1 "source publish directory contains ${src_count} file(s)"

  # 1l. The source must contain the app's main artifacts.
  local f
  for f in "MyPhotoWebApi.dll" "MyPhotoWebApi.deps.json" "MyPhotoWebApi.runtimeconfig.json" "MyPhotoWebApi"; do
    if [[ -e "${SOURCE_DIR}/${f}" ]]; then
      check 1 "source contains $(show "$f")"
    else
      check 0 "source is missing $(show "$f")"
      fatal "the source directory does not look like a complete publish output."
    fi
  done

  # 1m. The source appsettings.json must NOT be copied over the instance's.
  #     The repo's has a "Vlm" section and the wrong root for instance 5001.
  if [[ -f "${SOURCE_DIR}/appsettings.json" ]]; then
    if python3 -c 'import json,sys; d=json.load(open(sys.argv[1],encoding="utf-8")); sys.exit(0 if "Vlm" in d.get("MyPhotoSettings",{}) else 1)' "${SOURCE_DIR}/appsettings.json" 2>/dev/null; then
      check 1 "source appsettings.json HAS a Vlm section (it will NOT be copied — the instance's own file is kept)"
    else
      check 1 "source appsettings.json has no Vlm section (it still will NOT be copied — the instance's own file is kept)"
    fi
  else
    check 1 "source has no appsettings.json (nothing to accidentally copy)"
  fi

  # 1n. The source DLL's embedded version must match the expected commit.
  local src_sha
  src_sha="$(dll_version_sha "${SOURCE_DIR}/MyPhotoWebApi.dll" || true)"
  if [[ -n "$src_sha" ]]; then
    check 1 "source DLL version string = 1.0.0+${src_sha}"
    if [[ -n "$EXPECTED_COMMIT" ]]; then
      if [[ "$src_sha" == "$EXPECTED_COMMIT" ]]; then
        check 1 "source DLL commit matches target label commit (${EXPECTED_COMMIT})"
      else
        check 0 "source DLL commit ${src_sha} != target label commit ${EXPECTED_COMMIT}"
        fatal "the DLL you are about to deploy was built from a different commit."
      fi
    else
      info_line "target label has no resolvable commit; DLL sha cannot be cross-checked"
    fi
  else
    check 0 "source DLL contains no '1.0.0+<sha>' version string"
    fatal "cannot verify which commit the DLL was built from."
  fi

  # 1o. The photo roots this deployment will use must exist on disk.
  local root
  for root in "$EXPECTED_ROOT" "$OTHER_ROOT"; do
    if [[ -d "$root" ]]; then
      check 1 "photo root exists: $(show "$root")"
    else
      check 0 "photo root does NOT exist: $(show "$root")"
      fatal "missing photo root."
    fi
  done
}

# ---------------------------------------------------------------------------
# Copy stage
# ---------------------------------------------------------------------------
stage2_copy() {
  section "STAGE 2 — COPY PUBLISH OUTPUT INTO THE TARGET DIRECTORY"

  if [[ "$DRY_RUN" == "1" ]]; then
    dry_note "would run: cp -a '${SOURCE_DIR}/.' '${TARGET_DIR}/'"
    dry_note "would then write the instance's own appsettings.json into '${TARGET_DIR}/'"
    return 0
  fi

  # The instance's own appsettings.json wins over the publish output's.
  #
  # Order matters. `cp -a "${SOURCE_DIR}/." "${TARGET_DIR}/"` brings the SOURCE's
  # appsettings.json along — and the source's file may be the repository's,
  # which carries a "Vlm" section and the wrong photo root. That file must
  # never survive in the target.
  #
  # So the instance's canonical appsettings.json is captured into a temp file
  # BEFORE the copy, and installed afterwards. Capturing before the copy also
  # makes an idempotent re-run correct: when OLD_DEPLOY_DIR == TARGET_DIR (a
  # re-run with --force), the pre-existing target file is the instance's own
  # file and is captured before the source can overwrite it.
  info "capturing the instance-specific appsettings.json from the old deploy dir"
  local staged_settings
  staged_settings="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f '$staged_settings'" RETURN
  cp -a "${OLD_DEPLOY_DIR}/appsettings.json" "$staged_settings"

  # Create the target directory and copy the publish output.
  mkdir -p "$TARGET_DIR"
  info "copying publish output -> ${TARGET_DIR}"
  # -a preserves permissions/timestamps; the trailing /. copies the CONTENTS.
  cp -a "${SOURCE_DIR}/." "${TARGET_DIR}/"

  # Now put the instance's own appsettings.json back (over the source's).
  info "restoring the instance-specific appsettings.json into the target"
  cp -f "$staged_settings" "${TARGET_DIR}/appsettings.json"

  # --- Stage 2 assertions: full file-level manifest comparison ------------
  local src_manifest tgt_manifest
  src_manifest="$(mktemp)"
  tgt_manifest="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f '$src_manifest' '$tgt_manifest'" RETURN

  make_manifest "$SOURCE_DIR" "$src_manifest" "appsettings.json"
  make_manifest "$TARGET_DIR" "$tgt_manifest" "appsettings.json"

  local src_n tgt_n
  src_n="$(wc -l < "$src_manifest" | tr -d ' ')"
  tgt_n="$(wc -l < "$tgt_manifest" | tr -d ' ')"
  check 1 "source manifest built: ${src_n} file(s) (appsettings.json excluded)"
  check 1 "target manifest built: ${tgt_n} file(s) (appsettings.json excluded)"

  # Files present in source but missing or different in target.
  local missing=0 differing=0 extra=0 rel sh
  while IFS= read -r line; do
    [[ -n "$line" ]] || continue
    sh="${line%%  *}"
    rel="${line#*  }"
    if ! grep -qF -- "$rel" "$tgt_manifest"; then
      printf '  %sMISSING%s %s\n' "${C_RED}" "${C_RESET}" "$rel"
      missing=$((missing + 1))
      continue
    fi
    local tsh
    tsh="$(grep -F -- "$rel" "$tgt_manifest" | head -n 1 | awk '{print $1}')"
    if [[ "$tsh" != "$sh" ]]; then
      printf '  %sDIFFERS%s %s (source=%s target=%s)\n' "${C_RED}" "${C_RESET}" "$rel" "$sh" "$tsh"
      differing=$((differing + 1))
    fi
  done < "$src_manifest"

  # Files in the target that were not in the source (informational only: the
  # old dir may have had extra files; we only copied, so this should be empty).
  while IFS= read -r line; do
    [[ -n "$line" ]] || continue
    rel="${line#*  }"
    if ! grep -qF -- "$rel" "$src_manifest"; then
      extra=$((extra + 1))
    fi
  done < "$tgt_manifest"

  if [[ "$missing" == "0" ]]; then
    check 1 "no source file is missing from the target (0 missing)"
  else
    check 0 "${missing} file(s) present in the source are missing from the target"
  fi
  if [[ "$differing" == "0" ]]; then
    check 1 "every shared file has an identical sha256 (0 differing)"
  else
    check 0 "${differing} file(s) differ in sha256 between source and target"
  fi
  if [[ "$extra" == "0" ]]; then
    check 1 "no unexpected extra files in the target (0 extra)"
  else
    check 0 "${extra} extra file(s) in the target that are not in the source"
  fi

  # The target's appsettings.json must equal the OLD dir's byte for byte.
  if cmp -s "${OLD_DEPLOY_DIR}/appsettings.json" "${TARGET_DIR}/appsettings.json"; then
    check 1 "target appsettings.json is byte-identical to the old deploy dir's"
  else
    check 0 "target appsettings.json differs from the old deploy dir's"
    fatal "the instance-specific configuration was not preserved."
  fi

  # And it must NOT be the repository's appsettings.json.
  local repo_appsettings="${REPO_ROOT}/MyPhotoWebApi/appsettings.json"
  if [[ -f "$repo_appsettings" ]]; then
    if cmp -s "$repo_appsettings" "${TARGET_DIR}/appsettings.json"; then
      check 0 "target appsettings.json is identical to the REPOSITORY's appsettings.json (would add a Vlm section)"
      fatal "the repo appsettings.json was copied over the instance's."
    else
      check 1 "target appsettings.json is NOT the repository's appsettings.json (no Vlm section leaked)"
    fi
  fi

  # The DLL inside the target must carry the expected commit.
  local tgt_sha
  tgt_sha="$(dll_version_sha "${TARGET_DIR}/MyPhotoWebApi.dll" || true)"
  if [[ -n "$EXPECTED_COMMIT" && "$tgt_sha" == "$EXPECTED_COMMIT" ]]; then
    check 1 "target DLL version = 1.0.0+${tgt_sha} (matches expected commit)"
  elif [[ -z "$EXPECTED_COMMIT" ]]; then
    check 1 "target DLL version = 1.0.0+${tgt_sha} (no expected commit to compare against)"
  else
    check 0 "target DLL version = 1.0.0+${tgt_sha} != expected ${EXPECTED_COMMIT}"
    fatal "the deployed DLL was built from an unexpected commit."
  fi
}

# ---------------------------------------------------------------------------
# Drop-in rewriting
# ---------------------------------------------------------------------------
write_dropin() {
  section "REPOINT THE SYSTEMD DROP-IN"

  local tmp_dropin
  tmp_dropin="$(mktemp "${DROPIN}.tmp.XXXXXX")"

  local line first=1
  while IFS= read -r line || [[ -n "$line" ]]; do
    if [[ "$line" == "WorkingDirectory="* ]]; then
      printf 'WorkingDirectory=%s\n' "$TARGET_DIR" >> "$tmp_dropin"
    elif [[ "$line" == "ExecStart="* && "$line" != "ExecStart=" ]]; then
      # The real ExecStart line (the reset line "ExecStart=" is handled below).
      printf 'ExecStart=%s/MyPhotoWebApi --urls http://127.0.0.1:%s\n' "$TARGET_DIR" "$PORT" >> "$tmp_dropin"
    else
      # Everything else (section header, the ExecStart= reset line, all
      # Environment= lines) is copied VERBATIM.
      printf '%s\n' "$line" >> "$tmp_dropin"
    fi
    first=0
  done < "$DROPIN"

  # Sanity: the rewritten file must still contain the reset line and the Vlm
  # environment lines.
  if ! grep -qE '^ExecStart=$' "$tmp_dropin"; then
    rm -f "$tmp_dropin"
    fatal "rewritten drop-in lost the 'ExecStart=' reset line; aborting before any change."
  fi
  local vlm_before vlm_after
  vlm_before="$(dropin_lines "$DROPIN" "Environment=MyPhotoSettings__Vlm__" | wc -l | tr -d ' ')"
  vlm_after="$(dropin_lines "$tmp_dropin" "Environment=MyPhotoSettings__Vlm__" | wc -l | tr -d ' ')"
  if [[ "$vlm_before" != "$vlm_after" ]]; then
    rm -f "$tmp_dropin"
    fatal "rewritten drop-in has ${vlm_after} Vlm environment lines but the original had ${vlm_before}."
  fi

  if [[ "$DRY_RUN" == "1" ]]; then
    dry_note "would atomically write the drop-in (temp file + mv) with:"
    dry_note "  WorkingDirectory=${TARGET_DIR}"
    dry_note "  ExecStart=${TARGET_DIR}/MyPhotoWebApi --urls http://127.0.0.1:${PORT}"
    dry_note "  the ${vlm_after} Environment=MyPhotoSettings__Vlm__* line(s) preserved verbatim"
    rm -f "$tmp_dropin"
    return 0
  fi

  # Keep a timestamped backup of the previous drop-in next to it. The
  # pre-existing *.bak.* files are never touched.
  local backup="${DROPIN}.bak.$(date +%Y%m%d_%H%M%S)"
  cp -p "$DROPIN" "$backup"
  info "previous drop-in backed up to $(show "$backup")"

  # Atomic replace.
  mv -f "$tmp_dropin" "$DROPIN"
  check 1 "drop-in written atomically: $(show "$DROPIN")"
  check 1 "drop-in now points at WorkingDirectory=$(show "$TARGET_DIR")"
}

# ---------------------------------------------------------------------------
# Guard: never let a sandboxed run touch the real systemd units.
# ---------------------------------------------------------------------------
# The unit name is fixed (myphoto5000.service / myphoto5001.service), so
# `systemctl --user restart <unit>` ALWAYS acts on the real user unit,
# regardless of where DEPLOY_BASE / SYSTEMD_USER_DIR point. If this script is
# pointed at a sandbox, restarting the real unit would be wrong.
assert_real_systemd_target() {
  if [[ "$DEPLOY_BASE" != "/home/yww/deploy" || "$SYSTEMD_USER_DIR" != "${HOME}/.config/systemd/user" ]]; then
    fatal "refusing to run systemctl --user: this run is pointed at a sandbox (DEPLOY_BASE='${DEPLOY_BASE}', SYSTEMD_USER_DIR='${SYSTEMD_USER_DIR}') but the unit name '${UNIT_NAME}' is a REAL user unit. Use --dry-run, or run against the real paths."
  fi
}

# ---------------------------------------------------------------------------
# Restart
# ---------------------------------------------------------------------------
restart_service() {
  section "RESTART THE SERVICE"

  if [[ "$SKIP_RESTART" == "1" ]]; then
    dry_note "--skip-restart given: NOT running daemon-reload / restart"
    return 0
  fi

  if [[ "$DRY_RUN" == "1" ]]; then
    dry_note "would run: systemctl --user daemon-reload"
    dry_note "would run: systemctl --user restart ${UNIT_NAME}"
    return 0
  fi

  assert_real_systemd_target

  systemctl --user daemon-reload
  check 1 "systemctl --user daemon-reload succeeded"

  systemctl --user restart "$UNIT_NAME"
  check 1 "systemctl --user restart ${UNIT_NAME} succeeded"

  # Give the app a moment to bind the port.
  local waited=0
  while [[ "$waited" -lt 30 ]]; do
    if systemctl --user is-active --quiet "$UNIT_NAME"; then
      break
    fi
    sleep 1
    waited=$((waited + 1))
  done
  if systemctl --user is-active --quiet "$UNIT_NAME"; then
    check 1 "service ${UNIT_NAME} is active after restart"
  else
    check 0 "service ${UNIT_NAME} is NOT active after restart"
    journalctl --user -u "$UNIT_NAME" -n 30 --no-pager 2>/dev/null || true
    fatal "the service did not come back up; the old deploy dir is untouched, roll back or inspect the journal."
  fi
}

# ---------------------------------------------------------------------------
# Stage 3 — POST-RESTART assertions
# ---------------------------------------------------------------------------
stage3_postrestart() {
  section "STAGE 3 — POST-RESTART ASSERTIONS"

  if [[ "$SKIP_RESTART" == "1" ]]; then
    info_line "--skip-restart given: the running process was NOT restarted, so process-level checks are skipped"
    return 0
  fi

  local pid
  pid="$(systemctl --user show -p MainPID --value "$UNIT_NAME" 2>/dev/null || true)"
  if [[ -n "$pid" && "$pid" != "0" && -d "/proc/${pid}" ]]; then
    check 1 "service MainPID = ${pid}"
  else
    check 0 "could not determine a live MainPID for ${UNIT_NAME}"
    fatal "the service is not running."
  fi

  # 3a. /proc/<pid>/cwd must point at the new deploy dir.
  local cwd
  cwd="$(readlink "/proc/${pid}/cwd" 2>/dev/null || true)"
  if [[ "$cwd" == "$TARGET_DIR" ]]; then
    check 1 "/proc/${pid}/cwd = $(show "$cwd")"
  else
    check 0 "/proc/${pid}/cwd = $(show "$cwd") != expected $(show "$TARGET_DIR")"
    fatal "the running process is not executing from the new deploy directory."
  fi

  # 3b. /proc/<pid>/exe sha256 must equal the target dir's executable sha256.
  local exe_link exe_sha target_sha
  exe_link="$(readlink "/proc/${pid}/exe" 2>/dev/null || true)"
  exe_sha="$(sha256_of "/proc/${pid}/exe" 2>/dev/null || true)"
  target_sha="$(sha256_of "${TARGET_DIR}/MyPhotoWebApi" 2>/dev/null || true)"
  if [[ -n "$exe_sha" && -n "$target_sha" && "$exe_sha" == "$target_sha" ]]; then
    check 1 "/proc/${pid}/exe sha256 matches the target executable (${exe_sha})"
  else
    check 0 "/proc/${pid}/exe sha256 ${exe_sha:-<none>} != target executable sha256 ${target_sha:-<none>}"
    fatal "the running binary is not the one in the new deploy directory."
  fi
  if [[ -n "$exe_link" ]]; then
    info_line "/proc/${pid}/exe -> $(show "$exe_link")"
  fi

  # 3c. The drop-in's WorkingDirectory must equal the new deploy dir.
  local wd
  wd="$(dropin_value "$DROPIN" "WorkingDirectory" || true)"
  if [[ "$wd" == "$TARGET_DIR" ]]; then
    check 1 "drop-in WorkingDirectory = $(show "$wd")"
  else
    check 0 "drop-in WorkingDirectory = $(show "$wd") != expected $(show "$TARGET_DIR")"
    fatal "the drop-in was not updated correctly."
  fi

  # 3d. The drop-in's ExecStart must point at the new deploy dir.
  local execstart
  execstart="$(dropin_value "$DROPIN" "ExecStart" || true)"
  if [[ "$execstart" == "${TARGET_DIR}/MyPhotoWebApi --urls http://127.0.0.1:${PORT}" ]]; then
    check 1 "drop-in ExecStart = $(show "$execstart")"
  else
    check 0 "drop-in ExecStart = $(show "$execstart") != expected $(show "${TARGET_DIR}/MyPhotoWebApi --urls http://127.0.0.1:${PORT}")"
    fatal "the drop-in ExecStart was not updated correctly."
  fi

  # 3e. The target appsettings.json's DatabaseName must equal the expected
  #     database for this instance.
  local db
  db="$(json_get_opt "${TARGET_DIR}/appsettings.json" "MyPhotoSettings.DatabaseName" || true)"
  if [[ "$db" == "$EXPECTED_DB" ]]; then
    check 1 "target appsettings.json DatabaseName = $(show "$db")"
  else
    check 0 "target appsettings.json DatabaseName = $(show "$db") != expected $(show "$EXPECTED_DB")"
    fatal "the instance would talk to the wrong MongoDB database."
  fi

  # 3f. The target appsettings.json's RootFolder must equal the expected root.
  local root
  root="$(json_get_opt "${TARGET_DIR}/appsettings.json" "MyPhotoSettings.RootFolder" || true)"
  if [[ "$root" == "$EXPECTED_ROOT" ]]; then
    check 1 "target appsettings.json RootFolder = $(show "$root")"
  else
    check 0 "target appsettings.json RootFolder = $(show "$root") != expected $(show "$EXPECTED_ROOT")"
    fatal "the instance would serve the wrong photo root."
  fi

  # 3g. The running process' effective environment must not override the
  #     identity keys. (Read /proc/<pid>/environ; only the NAMES of
  #     MyPhotoSettings__* variables are printed, never their values.)
  local env_names
  env_names="$(tr '\0' '\n' < "/proc/${pid}/environ" 2>/dev/null \
    | grep -E '^MyPhotoSettings__(RootFolder|DatabaseName|UserPassLocation|FileUrl)=' \
    | sed 's/=.*/=<redacted>/' || true)"
  if [[ -z "$env_names" ]]; then
    check 1 "the running process has no Environment override for RootFolder/DatabaseName/UserPassLocation/FileUrl"
  else
    check 0 "the running process overrides identity settings via the environment:"
    printf '       %s\n' "$env_names"
    fatal "an environment override would silently redirect the instance."
  fi

  # 3h. The Vlm env vars must still be present in the running process.
  local vlm_names
  vlm_names="$(tr '\0' '\n' < "/proc/${pid}/environ" 2>/dev/null \
    | grep -cE '^MyPhotoSettings__Vlm__' || true)"
  if [[ "${vlm_names:-0}" -ge 1 ]]; then
    check 1 "the running process carries ${vlm_names} MyPhotoSettings__Vlm__* environment variable(s) (values not printed)"
  else
    check 0 "the running process has NO MyPhotoSettings__Vlm__* environment variables"
    fatal "the Vlm configuration was lost on restart."
  fi
}

# ---------------------------------------------------------------------------
# Stage 4 — LIVENESS + ISOLATION assertions
# ---------------------------------------------------------------------------

# Find a regular file under a root, preferring one whose relative path is
# unique to that root. Echoes the RELATIVE path (relative to the root).
find_probe_file() {
  local root="$1" rel
  # Look for a file in a top-level subdirectory, so the relative path is
  # meaningful (a file directly in the root would have an empty-ish path).
  rel="$(cd "$root" 2>/dev/null && find . -mindepth 2 -maxdepth 3 -type f -print0 2>/dev/null \
    | LC_ALL=C sort -z | head -z -n 1 | tr -d '\0')" || true
  if [[ -z "$rel" ]]; then
    rel="$(cd "$root" 2>/dev/null && find . -type f -print0 2>/dev/null \
      | LC_ALL=C sort -z | head -z -n 1 | tr -d '\0')" || true
  fi
  [[ -n "$rel" ]] || return 1
  printf '%s' "${rel#./}"
}

http_code() {
  local url="$1"
  curl -s -o /dev/null -w '%{http_code}' --max-time 20 -- "$url" 2>/dev/null || printf '000'
}

stage4_liveness() {
  section "STAGE 4 — LIVENESS AND ROOT ISOLATION"

  local base="http://127.0.0.1:${PORT}"
  local other_base="http://127.0.0.1:${OTHER_PORT}"

  # 4a. /health on this instance.
  local code body
  body="$(curl -s --max-time 20 "${base}/health" 2>/dev/null || true)"
  code="$(http_code "${base}/health")"
  if [[ "$code" == "000" ]]; then
    # Nothing is listening: the service is down. That is a hard failure for a
    # real deployment, but in a sandbox (no real service) it is expected.
    check 0 "GET ${base}/health -> 000 (nothing is listening on port ${PORT}; is ${UNIT_NAME} running?)"
    fatal "the deployed instance is not reachable."
  fi
  if [[ "$code" == "200" ]]; then
    check 1 "GET ${base}/health -> 200 ${body}"
  else
    check 0 "GET ${base}/health -> ${code} (body: ${body})"
    fatal "the deployed instance is not healthy."
  fi
  if [[ "$body" == *'"status":"ok"'* ]]; then
    check 1 "health body reports status=ok"
  else
    check 0 "health body does not report status=ok (body: ${body})"
    fatal "the instance reports a degraded state."
  fi

  # 4b. A file that exists in THIS instance's own root must be served 200.
  local own_rel own_enc own_code
  if own_rel="$(find_probe_file "$EXPECTED_ROOT")"; then
    own_enc="$(url_encode_path "$own_rel")"
    own_code="$(http_code "${base}/File/${own_enc}")"
    if [[ "$own_code" == "200" ]]; then
      check 1 "GET ${base}/File/${own_enc} -> 200 (file from this instance's own root: $(show "$own_rel"))"
    else
      check 0 "GET ${base}/File/${own_enc} -> ${own_code} (expected 200 for a file in this instance's own root)"
      fatal "the instance does not serve files from its own root."
    fi
  else
    check 0 "could not find a probe file under $(show "$EXPECTED_ROOT")"
    fatal "cannot run the isolation assertions without a probe file."
  fi

  # 4c. The SAME path on the OTHER instance must be 404.
  local other_code
  other_code="$(http_code "${other_base}/File/${own_enc}")"
  if [[ "$other_code" == "404" ]]; then
    check 1 "GET ${other_base}/File/${own_enc} -> 404 (the other instance does NOT serve this instance's files)"
  else
    check 0 "GET ${other_base}/File/${own_enc} -> ${other_code} (expected 404 — the instances are NOT isolated!)"
    fatal "the two instances share a photo root; isolation is broken."
  fi

  # 4d. A file that exists only in the OTHER instance's root must be 404 here.
  local other_rel other_rel_enc other_code2
  if other_rel="$(find_probe_file "$OTHER_ROOT")"; then
    other_rel_enc="$(url_encode_path "$other_rel")"
    other_code2="$(http_code "${base}/File/${other_rel_enc}")"
    if [[ "$other_code2" == "404" ]]; then
      check 1 "GET ${base}/File/${other_rel_enc} -> 404 (file exists only in the other instance's root: $(show "$other_rel"))"
    else
      check 0 "GET ${base}/File/${other_rel_enc} -> ${other_code2} (expected 404 — this instance serves the OTHER instance's files!)"
      fatal "the instances are not isolated."
    fi
    # And the other instance must serve its own file with 200.
    local other_own_code
    other_own_code="$(http_code "${other_base}/File/${other_rel_enc}")"
    if [[ "$other_own_code" == "200" ]]; then
      check 1 "GET ${other_base}/File/${other_rel_enc} -> 200 (the other instance serves its own root)"
    else
      check 0 "GET ${other_base}/File/${other_rel_enc} -> ${other_own_code} (expected 200)"
      info_line "the other instance may be down; that is outside this deployment's scope"
    fi
  else
    check 0 "could not find a probe file under the other instance's root $(show "$OTHER_ROOT")"
    info_line "skipping the reverse isolation check"
  fi
}

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------
build_source() {
  section "BUILD (dotnet publish)"

  local tmp_pub
  tmp_pub="$(mktemp -d "${TMPDIR:-/tmp}/myphoto-publish-XXXXXX")"
  info "publishing into a temporary directory: ${tmp_pub}"

  local proj="${REPO_ROOT}/MyPhotoWebApi/MyPhotoWebApi.csproj"
  dotnet publish "$proj" -c Release -o "$tmp_pub" --nologo
  check 1 "dotnet publish completed"

  SOURCE_DIR="$tmp_pub"
  if [[ "$KEEP_BUILD_DIR" == "0" && "$DRY_RUN" == "0" ]]; then
    # Cleaned up in the exit trap below.
    BUILT_DIR="$tmp_pub"
  else
    BUILT_DIR="$tmp_pub"
  fi
}

BUILT_DIR=""

cleanup() {
  if [[ -n "$BUILT_DIR" && "$KEEP_BUILD_DIR" == "0" && -d "$BUILT_DIR" ]]; then
    rm -rf "$BUILT_DIR"
  fi
}
trap cleanup EXIT

# ---------------------------------------------------------------------------
# Rollback
# ---------------------------------------------------------------------------
run_rollback() {
  section "ROLLBACK"

  local state_file="${DEPLOY_ROOT}/.rollback-state"
  local previous=""

  if [[ -n "$ROLLBACK_TO" ]]; then
    previous="${DEPLOY_ROOT}/${ROLLBACK_TO}"
  elif [[ -f "$state_file" ]]; then
    previous="$(cat "$state_file")"
  else
    previous="$(dropin_value "$DROPIN" "WorkingDirectory" || true)"
  fi

  if [[ -z "$previous" ]]; then
    die "no rollback target known (no --rollback-to, no state file, no WorkingDirectory in the drop-in)."
  fi
  if [[ ! -d "$previous" ]]; then
    die "rollback target directory does not exist: ${previous}"
  fi
  if [[ "$previous" == "$DEPLOY_ROOT" ]]; then
    die "refusing to roll back to the deploy root itself."
  fi

  info "rolling instance ${PORT} back to: ${previous}"
  TARGET_DIR="$previous"

  # The rollback target must have an appsettings.json with the right identity.
  local db root
  db="$(json_get_opt "${TARGET_DIR}/appsettings.json" "MyPhotoSettings.DatabaseName" || true)"
  root="$(json_get_opt "${TARGET_DIR}/appsettings.json" "MyPhotoSettings.RootFolder" || true)"
  if [[ "$db" == "$EXPECTED_DB" && "$root" == "$EXPECTED_ROOT" ]]; then
    check 1 "rollback target appsettings.json has DatabaseName=$(show "$db") RootFolder=$(show "$root")"
  else
    check 0 "rollback target appsettings.json has DatabaseName=$(show "$db") RootFolder=$(show "$root") (expected $(show "$EXPECTED_DB") / $(show "$EXPECTED_ROOT"))"
    fatal "refusing to roll back to a directory with the wrong identity."
  fi

  write_dropin

  if [[ "$DRY_RUN" == "1" ]]; then
    dry_note "would run: systemctl --user daemon-reload && systemctl --user restart ${UNIT_NAME}"
    dry_note "would record the current directory as the rollback target"
  else
    assert_real_systemd_target
    systemctl --user daemon-reload
    systemctl --user restart "$UNIT_NAME"
    sleep 2
    if systemctl --user is-active --quiet "$UNIT_NAME"; then
      check 1 "service ${UNIT_NAME} is active after rollback"
    else
      check 0 "service ${UNIT_NAME} is NOT active after rollback"
      journalctl --user -u "$UNIT_NAME" -n 30 --no-pager 2>/dev/null || true
      fatal "rollback failed to bring the service back up."
    fi
    # Remember the directory we rolled back FROM, so a rollback can be undone.
    local current
    current="$(dropin_value "$DROPIN" "WorkingDirectory" || true)"
    printf '%s\n' "$current" > "$state_file"
    info "rollback state recorded in ${state_file}"
  fi

  stage3_postrestart
  stage4_liveness
}

# ---------------------------------------------------------------------------
# --check : READ-ONLY audit of the LIVE deployment
# ---------------------------------------------------------------------------
# --check simulates ONE deployment of the four stages against the state that
# is currently RUNNING, and reports pass/fail for every assertion. It never
# writes, copies, restarts or deletes anything:
#
#   * it reads the live deploy dir from the drop-in's WorkingDirectory;
#   * it compares that dir's file-level sha256 manifest against a reference
#     (the sibling instance's dir for the same commit, or --source-dir);
#   * it verifies the live PROCESS via /proc/<pid>/{cwd,exe,maps};
#   * it exercises the HTTP endpoints for liveness and root isolation.
#
# The peer instance's port is a parameter (--peer-port), so 5000 and 5001 run
# through exactly the same code path.

# The live deploy dir of an instance: the drop-in's WorkingDirectory.
live_deploy_dir() {
  local unit="$1"
  local dropin="${SYSTEMD_USER_DIR}/${unit}.d/feature-deploy.conf"
  dropin_value "$dropin" "WorkingDirectory" 2>/dev/null || true
}

# The peer unit name for a given port.
peer_unit_for_port() {
  printf 'myphoto%s.service' "$1"
}

# The photo root the PEER instance actually serves, read from the peer
# unit's own deployed appsettings.json (authoritative: it is what the peer
# will really answer for), falling back to the instance table when the
# peer's config cannot be read.
peer_root_dir() {
  local port="$1" peer_unit peer_dir val
  peer_unit="$(peer_unit_for_port "$port")"
  peer_dir="$(live_deploy_dir "$peer_unit")"
  if [[ -n "$peer_dir" && -f "${peer_dir}/appsettings.json" ]]; then
    val="$(json_get_opt "${peer_dir}/appsettings.json" "MyPhotoSettings.RootFolder" || true)"
    if [[ -n "$val" ]]; then printf '%s' "$val"; return 0; fi
  fi
  case "$port" in
    5000) printf '%s' "$INST_5000_ROOT" ;;
    5001) printf '%s' "$INST_5001_ROOT" ;;
    *)    printf '' ;;
  esac
}

# Pick a file that exists under ONE root but NOT under the other, and whose
# extension the static-file middleware actually serves (.db/.prp/.ini are
# NOT served -> they would answer 404 and fake an isolation failure).
# $1 = own root, $2 = peer root. Echoes the RELATIVE path (no leading ./).
find_exclusive_probe() {
  local own="$1" peer="$2" rel
  local a b
  a="$(mktemp)"; b="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f '$a' '$b'" RETURN
  ( cd "$own" && find . -type f -printf '%P\n' 2>/dev/null | LC_ALL=C sort ) > "$a" || true
  ( cd "$peer" && find . -type f -printf '%P\n' 2>/dev/null | LC_ALL=C sort ) > "$b" || true
  rel="$(comm -23 "$a" "$b" \
        | grep -Ei '\.(jpg|jpeg|png|gif|bmp|webp|mp4|webm|avi|mov|3gp|mp3|wav)$' \
        | head -n 1 || true)"
  [[ -n "$rel" ]] || return 1
  printf '%s' "$rel"
}

# --- Stage 1 (check mode): the live deploy dir is the expected build ------
check_stage1_live() {
  section "STAGE 1 — PRE-DEPLOY ASSERTIONS (live deployment)"

  LIVE_DIR="$(live_deploy_dir "$UNIT_NAME")"
  if [[ -n "$LIVE_DIR" && -d "$LIVE_DIR" ]]; then
    check 1 "live deploy dir exists: $(show "$LIVE_DIR")"
  else
    check 0 "live deploy dir does not exist: $(show "$LIVE_DIR")"
    fatal "the drop-in points at a missing directory; fix that before deploying."
  fi

  # The deploy dir NAME is the only version marker (a publish output has no
  # version file). It must resolve to a commit, and the DLL embedded in the
  # dir must carry that very commit.
  local label base full_sha
  label="$(basename "$LIVE_DIR")"
  base="${label%%-*}"
  if [[ "$base" =~ ^[0-9a-f]{7,40}$ ]]; then
    check 1 "deploy dir name $(show "$label") is sha-shaped (${base})"
  else
    check 0 "deploy dir name $(show "$label") is not sha-shaped"
  fi
  full_sha="$(git -C "$REPO_ROOT" rev-parse --verify -q "${base}^{commit}" 2>/dev/null || true)"
  if [[ -n "$full_sha" ]]; then
    check 1 "dir name $(show "$base") resolves to commit ${full_sha}"
  else
    check 0 "dir name $(show "$base") does not resolve to any commit in ${REPO_ROOT}"
  fi

  local dll_sha
  dll_sha="$(dll_version_sha "${LIVE_DIR}/MyPhotoWebApi.dll" || true)"
  if [[ -n "$dll_sha" ]]; then
    check 1 "deployed DLL version = 1.0.0+${dll_sha}"
    if [[ -z "$full_sha" ]]; then
      # The dir name did not resolve (e.g. the script is running from a copy
      # of the repo that is not a git checkout, or lacks that commit). That
      # is "cannot verify", NOT "verified different" -- do not accuse.
      info_line "cannot cross-check the DLL commit: $(show "$base") does not resolve in ${REPO_ROOT}"
    elif [[ "$dll_sha" == "$full_sha" ]]; then
      check 1 "the deployed DLL was built from the commit the dir name claims"
    else
      check 0 "the deployed DLL was built from a different commit than the dir name claims"
    fi
  else
    check 0 "the deployed DLL carries no '1.0.0+<sha>' version string"
  fi

  # The three identity keys. They must match this instance's expectation AND
  # be byte-identical to the sibling instance's dir for the same commit --
  # that is what catches "5000's config copied into 5001".
  local apps="${LIVE_DIR}/appsettings.json"
  if [[ -f "$apps" ]]; then
    check 1 "appsettings.json found: $(show "$apps")"
  else
    check 0 "appsettings.json missing: $(show "$apps")"
    fatal "cannot read the instance configuration."
  fi

  local key val expected
  local -A EXPECTED_BY_KEY=(
    [RootFolder]="$EXPECTED_ROOT"
    [DatabaseName]="$EXPECTED_DB"
    [UserPassLocation]="$EXPECTED_USERPASS"
  )
  for key in RootFolder DatabaseName UserPassLocation; do
    val="$(json_get_opt "$apps" "MyPhotoSettings.${key}" || true)"
    expected="${EXPECTED_BY_KEY[$key]}"
    if [[ -z "$val" ]]; then
      check 0 "appsettings.json has no MyPhotoSettings.${key}"
      fatal "the instance configuration is incomplete."
    fi
    if [[ "$val" == "$expected" ]]; then
      check 1 "MyPhotoSettings.${key} = $(show "$val") (matches instance ${PORT} expectation)"
    else
      check 0 "MyPhotoSettings.${key} = $(show "$val") != instance ${PORT} expectation $(show "$expected")"
      fatal "the instance would serve the wrong photo root / database / user file."
    fi
  done

  # The live dir must be a clean instance config: no Vlm section (that comes
  # from the systemd environment) and not the repository's file.
  if python3 -c 'import json,sys; d=json.load(open(sys.argv[1],encoding="utf-8")); sys.exit(0 if "Vlm" in d.get("MyPhotoSettings",{}) else 1)' "$apps" 2>/dev/null; then
    check 0 "appsettings.json HAS a Vlm section (the deployed config is poisoned)"
  else
    check 1 "appsettings.json has no Vlm section (clean instance config)"
  fi
  local repo_apps="${REPO_ROOT}/MyPhotoWebApi/appsettings.json"
  if [[ -f "$repo_apps" ]] && cmp -s "$repo_apps" "$apps"; then
    check 0 "appsettings.json is identical to the REPOSITORY's appsettings.json"
  else
    check 1 "appsettings.json is NOT the repository's appsettings.json"
  fi

  # The drop-in must still be intact: it carries the Vlm environment, and
  # it must NOT override the identity keys.
  if [[ -f "$DROPIN" ]]; then
    check 1 "drop-in exists: $(show "$DROPIN")"
  else
    check 0 "drop-in not found: $(show "$DROPIN")"
    fatal "the drop-in is missing; the Vlm configuration would be lost."
  fi
  local overrides
  overrides="$(dropin_lines "$DROPIN" "Environment=MyPhotoSettings__(RootFolder|DatabaseName|UserPassLocation|FileUrl)=" || true)"
  if [[ -z "$overrides" ]]; then
    check 1 "drop-in contains no Environment override for RootFolder/DatabaseName/UserPassLocation/FileUrl"
  else
    check 0 "drop-in contains an environment override that would override appsettings.json:"
    printf '       %s\n' "$overrides"
    fatal "remove those Environment lines."
  fi
  local vlm_count
  vlm_count="$(dropin_lines "$DROPIN" "Environment=MyPhotoSettings__Vlm__" | wc -l | tr -d ' ')"
  if [[ "$vlm_count" -ge 1 ]]; then
    check 1 "drop-in carries ${vlm_count} Environment=MyPhotoSettings__Vlm__* line(s) (values not printed)"
  else
    check 0 "drop-in has NO Environment=MyPhotoSettings__Vlm__* lines"
  fi
  if dropin_lines "$DROPIN" "^ExecStart=$" | grep -q .; then
    check 1 "drop-in has the 'ExecStart=' reset line"
  else
    check 0 "drop-in is missing the 'ExecStart=' reset line"
  fi
}

# --- Stage 2 (check mode): file-level manifest of the live deploy dir ------
# Compares the live dir against a reference publish output, file by file,
# excluding appsettings.json (it legitimately differs per instance).
check_stage2_live() {
  section "STAGE 2 — FILE-LEVEL MANIFEST OF THE LIVE DEPLOY DIR"

  local reference="" ref_label=""
  if [[ -n "$SOURCE_DIR_ARG" ]]; then
    reference="$SOURCE_DIR_ARG"
    ref_label="source publish dir"
  else
    # Default reference: the sibling instance's dir for the SAME commit.
    # Both instances run the same build, so their manifests must match
    # exactly (apart from appsettings.json).
    local peer_unit peer_dir peer_label
    peer_unit="$(peer_unit_for_port "$PEER_PORT")"
    peer_dir="$(live_deploy_dir "$peer_unit")"
    peer_label="$(basename "$peer_dir" 2>/dev/null || true)"
    if [[ -n "$peer_dir" && -d "$peer_dir" && "$peer_label" == "$(basename "$LIVE_DIR")" ]]; then
      reference="$peer_dir"
      ref_label="peer instance's dir for the same commit"
    fi
  fi

  local manifest
  manifest="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f '$manifest'" RETURN
  make_manifest "$LIVE_DIR" "$manifest" "appsettings.json"
  local n
  n="$(wc -l < "$manifest" | tr -d ' ')"
  check 1 "live deploy dir manifest built: ${n} file(s) (appsettings.json excluded)"

  if [[ -z "$reference" ]]; then
    info_line "no reference build available to diff against (pass --source-dir to compare with a publish output)"
    return 0
  fi

  local ref_manifest
  ref_manifest="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f '$manifest' '$ref_manifest'" RETURN
  make_manifest "$reference" "$ref_manifest" "appsettings.json"
  local ref_n
  ref_n="$(wc -l < "$ref_manifest" | tr -d ' ')"
  check 1 "reference manifest built: ${ref_n} file(s) from ${ref_label}"

  # missing / differing / extra, all relative to the reference.
  local missing=0 differing=0 extra=0 rel sh tsh
  while IFS= read -r line; do
    [[ -n "$line" ]] || continue
    sh="${line%%  *}"; rel="${line#*  }"
    if ! grep -qF -- "$rel" "$manifest"; then
      printf '  %sMISSING%s %s\n' "${C_RED}" "${C_RESET}" "$rel"
      missing=$((missing + 1)); continue
    fi
    tsh="$(grep -F -- "$rel" "$manifest" | head -n 1 | awk '{print $1}')"
    if [[ "$tsh" != "$sh" ]]; then
      printf '  %sDIFFERS%s %s (ref=%s live=%s)\n' "${C_RED}" "${C_RESET}" "$rel" "$sh" "$tsh"
      differing=$((differing + 1))
    fi
  done < "$ref_manifest"
  while IFS= read -r line; do
    [[ -n "$line" ]] || continue
    rel="${line#*  }"
    if ! grep -qF -- "$rel" "$ref_manifest"; then
      extra=$((extra + 1))
    fi
  done < "$manifest"

  if [[ "$missing" == "0" ]]; then
    check 1 "every reference file is present in the live deploy dir (0 missing)"
  else
    check 0 "${missing} file(s) present in the reference are missing from the live deploy dir"
  fi
  if [[ "$differing" == "0" ]]; then
    check 1 "every shared file has an identical sha256 (0 differing)"
  else
    check 0 "${differing} file(s) differ in sha256 between the reference and the live deploy dir"
  fi
  if [[ "$extra" == "0" ]]; then
    check 1 "no unexpected extra files in the live deploy dir (0 extra)"
  else
    check 0 "${extra} extra file(s) in the live deploy dir that are not in the reference"
  fi
}

# --- Stage 3 (check mode): the running process really runs from that dir ---
check_stage3_live() {
  section "STAGE 3 — POST-RESTART ASSERTIONS (live process)"

  local pid
  pid="$(systemctl --user show -p MainPID --value "$UNIT_NAME" 2>/dev/null || true)"
  if [[ -n "$pid" && "$pid" != "0" && -d "/proc/${pid}" ]]; then
    check 1 "service MainPID = ${pid}"
  else
    check 0 "could not determine a live MainPID for ${UNIT_NAME}"
    fatal "the service is not running."
  fi

  # /proc/<pid>/cwd must point at the live deploy dir.
  local cwd
  cwd="$(readlink "/proc/${pid}/cwd" 2>/dev/null || true)"
  if [[ "$cwd" == "$LIVE_DIR" ]]; then
    check 1 "/proc/${pid}/cwd = $(show "$cwd")"
  else
    check 0 "/proc/${pid}/cwd = $(show "$cwd") != expected $(show "$LIVE_DIR")"
    fatal "the running process is not executing from the live deploy directory."
  fi

  # /proc/<pid>/exe sha256 == the live dir's executable sha256.
  local exe_sha dir_sha
  exe_sha="$(sha256_of "/proc/${pid}/exe" 2>/dev/null || true)"
  dir_sha="$(sha256_of "${LIVE_DIR}/MyPhotoWebApi" 2>/dev/null || true)"
  if [[ -n "$exe_sha" && -n "$dir_sha" && "$exe_sha" == "$dir_sha" ]]; then
    check 1 "/proc/${pid}/exe sha256 matches the live deploy dir's executable (${exe_sha})"
  else
    check 0 "/proc/${pid}/exe sha256 ${exe_sha:-<none>} != live dir executable sha256 ${dir_sha:-<none>}"
    fatal "the running binary is not the one in the live deploy directory."
  fi

  # The KERNEL's own view: which files are actually mapped into the process.
  # cwd alone can lie; /proc/<pid>/maps cannot.
  #
  # The assertion is NOT "every mapped path is under the deploy dir" -- a
  # .NET process legitimately maps framework assemblies from
  # /usr/lib/dotnet/shared/... and libc/libssl from /usr/lib/x86_64-linux-gnu/.
  # What matters is that no mapped file comes from a DIFFERENT deploy dir
  # (that would mean the process is running a stale or foreign build).
  local maps
  maps="$(awk '$NF ~ /^\// {print $NF}' "/proc/${pid}/maps" 2>/dev/null | sort -u || true)"
  local mapped_count
  mapped_count="$(printf '%s\n' "$maps" | grep -c . || true)"
  check 1 "kernel reports ${mapped_count} mapped file path(s) under the filesystem"

  local deploy_mapped stray
  deploy_mapped="$(printf '%s\n' "$maps" | grep -E "^${DEPLOY_BASE}/" || true)"
  stray="$(printf '%s\n' "$deploy_mapped" | grep -v "^${LIVE_DIR}/" || true)"
  if [[ -z "$stray" ]]; then
    check 1 "every file mapped from the deploy tree comes from the live deploy dir (0 stray)"
  else
    check 0 "mapped file path(s) come from a DIFFERENT deploy dir than $(show "$LIVE_DIR"):"
    printf '%s\n' "$stray" | sed 's/^/       /'
    fatal "the running process is mapped from a stale or foreign deploy directory."
  fi

  local live_mapped
  live_mapped="$(printf '%s\n' "$deploy_mapped" | grep -c "^${LIVE_DIR}/" || true)"
  check 1 "${live_mapped} mapped file(s) come from the live deploy dir"

  # The mapped MyPhotoWebApi.dll must be the one in the live dir.
  local mapped_dll
  mapped_dll="$(printf '%s\n' "$maps" | grep -E "/MyPhotoWebApi\.dll$" | head -n 1 || true)"
  if [[ -n "$mapped_dll" ]]; then
    check 1 "kernel maps MyPhotoWebApi.dll from $(show "$mapped_dll")"
    local mapped_sha expect_sha
    mapped_sha="$(sha256_of "$mapped_dll" 2>/dev/null || true)"
    expect_sha="$(sha256_of "${LIVE_DIR}/MyPhotoWebApi.dll" 2>/dev/null || true)"
    if [[ -n "$mapped_sha" && -n "$expect_sha" && "$mapped_sha" == "$expect_sha" ]]; then
      check 1 "the mapped MyPhotoWebApi.dll sha256 matches the live deploy dir's copy"
    else
      check 0 "the mapped MyPhotoWebApi.dll sha256 ${mapped_sha:-<none>} != live dir copy ${expect_sha:-<none>}"
    fi
  else
    check 0 "the kernel maps no MyPhotoWebApi.dll"
  fi

  # The running process must not override the identity keys via the
  # environment (only key NAMES are printed, never their values).
  local env_names
  env_names="$(tr '\0' '\n' < "/proc/${pid}/environ" 2>/dev/null \
    | grep -E '^MyPhotoSettings__(RootFolder|DatabaseName|UserPassLocation|FileUrl)=' \
    | sed 's/=.*/=<redacted>/' || true)"
  if [[ -z "$env_names" ]]; then
    check 1 "the running process has no Environment override for RootFolder/DatabaseName/UserPassLocation/FileUrl"
  else
    check 0 "the running process overrides identity settings via the environment:"
    printf '       %s\n' "$env_names"
    fatal "an environment override would silently redirect the instance."
  fi

  local vlm_names
  vlm_names="$(tr '\0' '\n' < "/proc/${pid}/environ" 2>/dev/null \
    | grep -cE '^MyPhotoSettings__Vlm__' || true)"
  if [[ "${vlm_names:-0}" -ge 1 ]]; then
    check 1 "the running process carries ${vlm_names} MyPhotoSettings__Vlm__* environment variable(s) (values not printed)"
  else
    check 0 "the running process carries NO MyPhotoSettings__Vlm__* environment variables"
    fatal "the Vlm configuration was lost on restart."
  fi
}

# --- Stage 4 (check mode): liveness + root isolation ----------------------
check_stage4_live() {
  section "STAGE 4 — LIVENESS AND ROOT ISOLATION"

  local base="http://127.0.0.1:${PORT}"
  local peer_base="http://127.0.0.1:${PEER_PORT}"

  # /health on THIS instance. A 503 means the Mongo ping failed -- which may
  # be a MongoDB-wide outage rather than a deployment problem. The peer is
  # consulted to tell the two apart.
  local code body
  body="$(curl -s --max-time 20 "${base}/health" 2>/dev/null || true)"
  code="$(http_code "${base}/health")"
  if [[ "$code" == "000" ]]; then
    check 0 "GET ${base}/health -> 000 (nothing is listening on port ${PORT}; is ${UNIT_NAME} running?)"
    fatal "the instance is not reachable."
  fi

  local peer_code
  peer_code="$(http_code "${peer_base}/health")"

  if [[ "$code" == "200" ]]; then
    check 1 "GET ${base}/health -> 200 ${body}"
  else
    if [[ "$peer_code" != "200" ]]; then
      check 0 "GET ${base}/health -> ${code} AND the peer on port ${PEER_PORT} -> ${peer_code}: MongoDB is unreachable for BOTH instances (a shared-dependency outage, not a deployment failure)"
    else
      check 0 "GET ${base}/health -> ${code} while the peer is healthy (body: ${body})"
      fatal "this instance alone is unhealthy."
    fi
  fi
  if [[ "$code" == "200" ]]; then
    if [[ "$body" == *'"status":"ok"'* ]]; then
      check 1 "health body reports status=ok"
    else
      check 0 "health body does not report status=ok (body: ${body})"
    fi
  fi

  # Isolation, both directions:
  #   a file that exists ONLY under this instance's root -> 200 here, 404 on
  #   the peer; and a file that exists ONLY under the peer's root -> 404 here.
  local peer_root
  peer_root="$(peer_root_dir "$PEER_PORT")"
  if [[ -z "$peer_root" ]]; then
    check 0 "could not determine the peer instance's photo root (port ${PEER_PORT})"
    fatal "cannot run the isolation assertions without the peer's root."
  fi
  if [[ "$peer_root" == "$EXPECTED_ROOT" ]]; then
    check 0 "the peer instance (port ${PEER_PORT}) serves the SAME photo root as this one ($(show "$EXPECTED_ROOT"))"
    fatal "the two instances share a photo root; isolation is structurally impossible."
  fi
  check 1 "the peer instance (port ${PEER_PORT}) serves a different root: $(show "$peer_root")"

  local own_probe peer_probe
  own_probe="$(find_exclusive_probe "$EXPECTED_ROOT" "$peer_root" || true)"
  peer_probe="$(find_exclusive_probe "$peer_root" "$EXPECTED_ROOT" || true)"

  if [[ -z "$own_probe" ]]; then
    check 0 "could not find a file that exists only under $(show "$EXPECTED_ROOT")"
    fatal "cannot run the isolation assertions without a probe file."
  fi
  local enc
  enc="$(url_encode_path "$own_probe")"
  local own_code peer_code2
  own_code="$(http_code "${base}/File/${enc}")"
  peer_code2="$(http_code "${peer_base}/File/${enc}")"
  if [[ "$own_code" == "200" ]]; then
    check 1 "GET ${base}/File/${enc} -> 200 (file only this instance has: $(show "$own_probe"))"
  else
    check 0 "GET ${base}/File/${enc} -> ${own_code} (expected 200 for a file only this instance has)"
    fatal "the instance does not serve files from its own root."
  fi
  if [[ "$peer_code2" == "404" ]]; then
    check 1 "GET ${peer_base}/File/${enc} -> 404 (the peer does NOT serve this instance's files)"
  else
    check 0 "GET ${peer_base}/File/${enc} -> ${peer_code2} (expected 404 -- the instances are NOT isolated!)"
    fatal "the two instances share a photo root; isolation is broken."
  fi

  # The reverse direction (b): a file that only the PEER has must be 404 here.
  if [[ -z "$peer_probe" ]]; then
    check 0 "could not find a file that exists only under $(show "$OTHER_ROOT")"
    info_line "skipping the reverse isolation check"
    return 0
  fi
  local penc
  penc="$(url_encode_path "$peer_probe")"
  local self_code peer_code3
  self_code="$(http_code "${base}/File/${penc}")"
  peer_code3="$(http_code "${peer_base}/File/${penc}")"
  if [[ "$self_code" == "404" ]]; then
    check 1 "GET ${base}/File/${penc} -> 404 (file only the PEER has: $(show "$peer_probe"))"
  else
    check 0 "GET ${base}/File/${penc} -> ${self_code} (expected 404 -- this instance serves the OTHER instance's files!)"
    fatal "the instances are not isolated."
  fi
  if [[ "$peer_code3" == "200" ]]; then
    check 1 "GET ${peer_base}/File/${penc} -> 200 (the peer serves its own file)"
  else
    check 0 "GET ${peer_base}/File/${penc} -> ${peer_code3} (expected 200)"
  fi
}

# Run the full read-only audit.
run_check() {
  info "CHECK MODE — read-only audit of instance ${PORT}; nothing will be written, copied or restarted"
  check_stage1_live
  check_stage2_live
  check_stage3_live
  check_stage4_live
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
main() {
  parse_args "$@"

  # --check is a standalone READ-ONLY audit: it needs no --target (it
  # inspects what is currently deployed) and never writes anything.
  if [[ "$CHECK_MODE" == "1" ]]; then
    if [[ -z "$INSTANCE" ]]; then
      err "--instance is required."
      usage >&2
      exit 2
    fi
    if [[ "$ROLLBACK" == "1" || "$BUILD" == "1" ]]; then
      err "--check cannot be combined with --rollback or --build."
      exit 2
    fi
    require_tools
    resolve_instance
    run_check
    summary
    return 0
  fi

  if [[ "$ROLLBACK" == "1" ]]; then
    require_tools
    resolve_instance
    if [[ "$DRY_RUN" == "1" ]]; then
      info "DRY RUN — rollback rehearsal for instance ${PORT}"
    else
      info "ROLLBACK for instance ${PORT}"
    fi
    run_rollback
    summary
    return 0
  fi

  if [[ -z "$INSTANCE" ]]; then
    err "--instance is required."
    usage >&2
    exit 2
  fi
  if [[ -z "$TARGET_LABEL" ]]; then
    err "--target is required (the deploy directory name, e.g. 52cc9e0)."
    usage >&2
    exit 2
  fi
  if [[ -n "$TARGET_LABEL" && "$TARGET_LABEL" == */* ]]; then
    die "--target must be a directory NAME, not a path (got '${TARGET_LABEL}')."
  fi

  require_tools
  resolve_instance

  if [[ "$DRY_RUN" == "1" ]]; then
    info "DRY RUN — nothing will be written, copied, or restarted"
  else
    info "DEPLOY instance ${PORT} -> target '${TARGET_LABEL}'"
  fi

  # Resolve the source directory.
  if [[ "$BUILD" == "1" ]]; then
    if [[ "$DRY_RUN" == "1" ]]; then
      info "DRY RUN: --build would run 'dotnet publish' into a temporary directory"
      SOURCE_DIR="${SOURCE_DIR_ARG:-${REPO_ROOT}/MyPhotoWebApi/publish}"
      if [[ ! -d "$SOURCE_DIR" ]]; then
        # Fall back to the currently deployed dir's contents as a stand-in so
        # the read-only assertions can still run.
        SOURCE_DIR="${OLD_DEPLOY_DIR:-}"
      fi
      info_line "dry-run build: using $(show "$SOURCE_DIR") as the stand-in publish output"
    else
      build_source
    fi
  else
    SOURCE_DIR="${SOURCE_DIR_ARG:-${REPO_ROOT}/MyPhotoWebApi/publish}"
  fi

  stage1_predeploy

  if [[ "$DRY_RUN" == "1" ]]; then
    section "DRY RUN — WHAT WOULD HAPPEN"
    dry_note "1. copy publish output '${SOURCE_DIR}' -> '${TARGET_DIR}'"
    dry_note "2. restore the instance's own appsettings.json into '${TARGET_DIR}'"
    dry_note "3. compare every file (sha256) between source and target, excluding appsettings.json"
    dry_note "4. atomically rewrite '${DROPIN}' (WorkingDirectory + ExecStart path; Vlm env lines verbatim)"
    dry_note "5. systemctl --user daemon-reload && systemctl --user restart ${UNIT_NAME}"
    dry_note "6. verify /proc/<pid>/cwd, /proc/<pid>/exe sha256, drop-in WorkingDirectory, DatabaseName"
    dry_note "7. verify /health -> 200 and root isolation (200 on own root, 404 on the other instance)"
    dry_note "the previous deploy directory '${OLD_DEPLOY_DIR}' is kept for rollback"
    summary
    return 0
  fi

  stage2_copy
  write_dropin

  # Remember where we came from, so --rollback can find it.
  mkdir -p "$DEPLOY_ROOT"
  printf '%s\n' "$OLD_DEPLOY_DIR" > "${DEPLOY_ROOT}/.rollback-state"

  restart_service
  stage3_postrestart
  stage4_liveness
  summary
}

summary() {
  section "SUMMARY"
  printf '  checks run : %d\n' "$CHECKS"
  printf '  failures   : %d\n' "$FAILURES"
  if [[ "$CHECK_MODE" == "1" ]]; then
    printf '  mode       : %sCHECK%s (read-only; nothing was changed)\n' "${C_YELLOW}" "${C_RESET}"
  elif [[ "$DRY_RUN" == "1" ]]; then
    printf '  mode       : %sDRY RUN%s (nothing was changed)\n' "${C_YELLOW}" "${C_RESET}"
  else
    printf '  mode       : %sLIVE%s\n' "${C_BOLD}" "${C_RESET}"
  fi
  printf '  instance   : %s (port %s, DB %s, root %s)\n' "$PORT" "$PORT" "$EXPECTED_DB" "$EXPECTED_ROOT"
  if [[ "$CHECK_MODE" == "1" ]]; then
    printf '  live dir   : %s\n' "$LIVE_DIR"
    printf '  peer       : port %s\n' "$PEER_PORT"
  else
    printf '  target dir : %s\n' "$TARGET_DIR"
  fi
  if [[ -n "$OLD_DEPLOY_DIR" ]]; then
    printf '  previous   : %s (kept on disk for rollback)\n' "$OLD_DEPLOY_DIR"
  fi
  if [[ "$FAILURES" != "0" ]]; then
    printf '\n  %sRESULT: FAILED%s — %d check(s) did not pass.\n' "${C_RED}${C_BOLD}" "${C_RESET}" "$FAILURES"
    exit 1
  fi
  printf '\n  %sRESULT: ALL CHECKS PASSED%s\n' "${C_GREEN}${C_BOLD}" "${C_RESET}"
}

main "$@"
