set -e  # Stop (with a non-zero exit code) if the tests or the build fail

reldir="$(dirname "$0")"
cd "$reldir"
BUILD_CONFIGURATION="release"
DOTNET_ARTIFACTS_DIR="artifacts"

RELAY_ARTIFACTS="$DOTNET_ARTIFACTS_DIR/bin/relay/$BUILD_CONFIGURATION"

# Project paths
RELAY_PROJ="runtime/relay.csproj"
RELAY_TESTS_PROJ="tests/relay_tests.csproj"

rm -rf $DOTNET_ARTIFACTS_DIR

#https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-build

dotnet clean $RELAY_PROJ
dotnet clean $RELAY_TESTS_PROJ

dotnet test $RELAY_TESTS_PROJ

dotnet build $RELAY_PROJ --no-incremental --configuration $BUILD_CONFIGURATION --force --artifacts-path $DOTNET_ARTIFACTS_DIR

echo "Build output: $RELAY_ARTIFACTS/"
