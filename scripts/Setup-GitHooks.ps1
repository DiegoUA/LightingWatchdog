$hookDir = Join-Path $PSScriptRoot "..\.git\hooks"
    $hookFile = Join-Path $hookDir "pre-commit"

    if (-Not (Test-Path $hookDir)) {
        Write-Error "Could not find .git/hooks directory. Are you in the repository root?"
        exit 1
    }

    $hookContent = @"
    #!/bin/sh
    echo "========================================"
    echo "Running pre-commit .NET build check..."
    echo "========================================"

    dotnet build src/NetworkWatchdogService/NetworkWatchdogService.csproj -q

    if [ `$? -ne 0 ]; then
        echo ""
        echo "❌ BUILD FAILED! Commit aborted."
        echo "Please fix the compiler errors above before committing."
        exit 1
    fi

    echo "✅ Build passed! Proceeding with commit."
    exit 0
"@

    Set-Content -Path $hookFile -Value $hookContent -Encoding utf8
    Write-Host "Pre-commit hook installed successfully at $hookFile" -ForegroundColor Green