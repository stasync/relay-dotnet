set -e  # Stop (with a non-zero exit code) if the tests or the build fail

reldir="$(dirname "$0")"
cd "$reldir"
BUILD_CONFIGURATION="release"
DOTNET_ARTIFACTS_DIR="artifacts"

RT_SERVER_ARTIFACTS="$DOTNET_ARTIFACTS_DIR/bin/rt_server/$BUILD_CONFIGURATION"

# Project paths
RT_SERVER_PROJ="runtime/rt_server.csproj"
RT_SERVER_TESTS_PROJ="tests/rt_server_tests.csproj"

rm -rf $DOTNET_ARTIFACTS_DIR

#https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-build

dotnet clean $RT_SERVER_PROJ
dotnet clean $RT_SERVER_TESTS_PROJ

dotnet test $RT_SERVER_TESTS_PROJ

dotnet build $RT_SERVER_PROJ --no-incremental --configuration $BUILD_CONFIGURATION --force --artifacts-path $DOTNET_ARTIFACTS_DIR

echo "Build output: $RT_SERVER_ARTIFACTS/"
