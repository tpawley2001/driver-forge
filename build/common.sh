# Shared by the build scripts. Machine names and the Windows account come from
# build/hosts.env (not committed; copy hosts.env.example).
#   BUILD_HOST  ssh name of the Windows box that builds (needs .NET SDK, Inno Setup 6)
#   TEST_HOST   ssh name of the Windows box that runs test installs (never a daily-use PC)
#   WIN_USER    the Windows account on both, which holds the working folders
COMMON_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=/dev/null
[ -f "$COMMON_DIR/hosts.env" ] && . "$COMMON_DIR/hosts.env"
BUILD_HOST=${BUILD_HOST:-buildbox}
TEST_HOST=${TEST_HOST:-testbox}
: "${WIN_USER:?set WIN_USER in build/hosts.env (see build/hosts.env.example)}"
BUILD_DIR="C:/Users/$WIN_USER/driverforge-dev"      # on BUILD_HOST
TEST_DIR="C:\\Users\\$WIN_USER\\driverforge-test"   # on TEST_HOST

# winps HOST SCRIPT: run PowerShell there without quoting games. -EncodedCommand
# takes base64 UTF-16LE, so nothing in between (bash, ssh, cmd.exe) can mangle it.
winps() {
  local host=$1 script="\$ProgressPreference='SilentlyContinue'
$2"
  ssh "$host" "powershell -NoProfile -NonInteractive -EncodedCommand $(printf '%s' "$script" | iconv -f UTF-8 -t UTF-16LE | base64 -w0)"
}
