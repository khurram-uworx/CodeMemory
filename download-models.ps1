#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Downloads the bge-micro-v2 ONNX embedding model from HuggingFace.
.DESCRIPTION
    Downloads model.onnx and vocab.txt for the bge-micro-v2 BERT embedding model
    (TaylorAI/bge-micro-v2, Apache-2.0 license). Places them in the models/
    directory for use by CodeMemory.AspNet with Embedding:Provider = "onnx"
    or "sk-connector-onnx".
.EXAMPLE
    ./download-models.ps1
#>

$ModelDir = Join-Path $PSScriptRoot "models" "bge-micro-v2"
New-Item -ItemType Directory -Path $ModelDir -Force | Out-Null

$ModelUrl = "https://huggingface.co/TaylorAI/bge-micro-v2/resolve/main/onnx/model.onnx"
$VocabUrl = "https://huggingface.co/TaylorAI/bge-micro-v2/resolve/main/vocab.txt"

$ModelPath = Join-Path $ModelDir "model.onnx"
$VocabPath = Join-Path $ModelDir "vocab.txt"

function Download-File {
    param($Url, $Path, $Description)

    if (Test-Path $Path) {
        Write-Host "✓ $Description already exists, skipping." -ForegroundColor Green
        return
    }

    Write-Host "↓ Downloading $Description ..." -ForegroundColor Cyan
    try {
        $wc = New-Object System.Net.WebClient
        $wc.DownloadFile($Url, $Path)
        Write-Host "✓ $Description saved to $Path" -ForegroundColor Green
    }
    catch {
        Write-Host "✗ Failed to download $Description : $_" -ForegroundColor Red
        exit 1
    }
}

Download-File -Url $ModelUrl -Path $ModelPath -Description "model.onnx (~69 MB)"
Download-File -Url $VocabUrl -Path $VocabPath -Description "vocab.txt"

Write-Host ""
Write-Host "=== Download complete ===" -ForegroundColor Green
Write-Host "Model: $ModelPath" -ForegroundColor Gray
Write-Host "Vocab: $VocabPath" -ForegroundColor Gray
Write-Host ""
Write-Host "To use with CodeMemory.AspNet, set:" -ForegroundColor Yellow
Write-Host '  "Embedding": { "Provider": "onnx" }' -ForegroundColor White
Write-Host "in appsettings.json" -ForegroundColor Yellow
