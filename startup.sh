#!/bin/sh
#
# startup.sh — Build and run the relay in the foreground. Stop it with Ctrl+C.
#
# Usage: sh startup.sh [extra relay arguments]
#
#   sh startup.sh                                       # HTTP API on http://127.0.0.1:8080/
#   sh startup.sh -relayPort 7777                       # also open an unprotected relay on 7777 (testing only)
#   HTTP_LISTENER_URL=http://127.0.0.1:9000/ sh startup.sh

cd "$(dirname "$0")"
HTTP_LISTENER_URL="${HTTP_LISTENER_URL:-http://127.0.0.1:8080/}" # HTTP control API address, must end with '/'

dotnet run --project runtime/relay.csproj --configuration Release -- -httpListenerUrl "$HTTP_LISTENER_URL" "$@"
