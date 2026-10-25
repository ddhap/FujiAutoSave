#!/usr/bin/env pwsh
$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot

& bash (Join-Path $Root "scripts/build-fn.sh")
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
