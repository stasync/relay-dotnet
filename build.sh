reldir="$(dirname "$0")"
cd "$reldir"
BUILD_CONFIGURATION="release"
DOTNET_ARTIFACTS_DIR="artifacts"
OUT_DIR="build/bin/"

OUT_RELAY_DIR="$OUT_DIR/rt_server/"

RT_SERVER_ARTIFACTS="$DOTNET_ARTIFACTS_DIR/bin/rt_server/$BUILD_CONFIGURATION"

# Project paths
RT_SERVER_PROJ="runtime/rt_server.csproj"
RT_SERVER_TESTS_PROJ="tests/rt_server_tests.csproj"

rm -rf $DOTNET_ARTIFACTS_DIR
rm -rf $OUT_DIR

#https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-build

dotnet clean $RT_SERVER_PROJ
dotnet clean $RT_SERVER_TESTS_PROJ

dotnet test $RT_SERVER_TESTS_PROJ

dotnet build $RT_SERVER_PROJ --no-incremental --configuration $BUILD_CONFIGURATION --force --artifacts-path $DOTNET_ARTIFACTS_DIR

# Copy built rt_server files to build folder
echo "Copying rt_server (RELAY) artifacts to build folder..."
if [ -d "$RT_SERVER_ARTIFACTS/" ]; then
    mkdir -p $OUT_RELAY_DIR/
    cp -r $RT_SERVER_ARTIFACTS/* $OUT_RELAY_DIR/
    echo "Copy completed successfully."
else
    echo -e "\033[1;31mError: Source directory $RT_SERVER_ARTIFACTS/ does not exist!\033[0m"
    exit 1
fi
