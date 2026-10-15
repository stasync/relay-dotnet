#!/bin/sh
#
# startup.sh — Build and run a relay node in the foreground. Stop it with Ctrl+C.
#
# Usage: sh startup.sh [extra node arguments]
#
#   sh startup.sh                                       # HTTP API on http://127.0.0.1:8080/, with a new random API key
#   HTTP_API_KEY=<key> sh startup.sh                    # use your own API key
#   sh startup.sh -roomPort 7777                        # also open an unprotected room on 7777 (testing only)
#   HTTP_LISTENER_URL=http://127.0.0.1:9000/ sh startup.sh

cd "$(dirname "$0")"
HTTP_LISTENER_URL="${HTTP_LISTENER_URL:-http://127.0.0.1:8080/}" # HTTP control API address, must end with '/'
HTTP_API_KEY="${HTTP_API_KEY:-$(openssl rand -hex 32)}"        # every HTTP request must send it as 'Authorization: Bearer <key>'

echo "HTTP API key: $HTTP_API_KEY"

dotnet run --project runtime/relay.csproj --configuration Release -- -httpListenerUrl "$HTTP_LISTENER_URL" -httpApiKey "$HTTP_API_KEY" "$@"
