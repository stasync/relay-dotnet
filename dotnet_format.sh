#!/bin/bash

# Format all C# projects in the repository
# This script finds all .csproj files and runs dotnet format on each

set -e  # Exit on error

echo "Finding all .csproj files..."
PROJECTS=$(find . -name "*.csproj" -not -path "*/obj/*" -not -path "*/bin/*")

if [ -z "$PROJECTS" ]; then
    echo "No .csproj files found!"
    exit 1
fi

echo "Found projects:"
echo "$PROJECTS"
echo ""

for project in $PROJECTS; do
    echo ""
    echo -e "\033[0;32mFormatting: $project\033[0m"
    dotnet format "$project" --verbosity diagnostic
    echo ""
done

echo -e "\033[0;32mAll projects formatted successfully!\033[0m"
