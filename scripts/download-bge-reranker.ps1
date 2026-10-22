<#
.SYNOPSIS
    Downloads official BGE-Reranker-v2-m3 ONNX model assets into Models/bge-reranker-v2-m3/.
.DESCRIPTION
    Bootstraps local cross-encoder model storage for Pigmentor production reranking.
    Downloads the quantized int8 model (~560 MB) or official FP32 model from Hugging Face.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$TargetDir,

    [switch]$FP32,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    $TargetDir = Join-Path $PSScriptRoot "..\Models\bge-reranker-v2-m3"
}

$resolvedDir = [System.IO.Path]::GetFullPath($TargetDir)
if (-not (Test-Path $resolvedDir)) {
    New-Item -ItemType Directory -Path $resolvedDir -Force | Out-Null
}

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " Pigmentor BGE-Reranker-v2-m3 Model Downloader" -ForegroundColor Cyan
Write-Host " Target directory: $resolvedDir" -ForegroundColor Cyan
Write-Host " Mode: $(if ($FP32) { 'Official FP32 (~2.2 GB)' } else { 'Quantized int8 (~560 MB)' })" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

if ($FP32) {
    $artifacts = @(
        @{ Name = "model.onnx";              Url = "https://huggingface.co/BAAI/bge-reranker-v2-m3/resolve/main/onnx/model.onnx";                    MinBytes = 500000 },
        @{ Name = "model.onnx_data";         Url = "https://huggingface.co/BAAI/bge-reranker-v2-m3/resolve/main/onnx/model.onnx_data";               MinBytes = 2000000000 },
        @{ Name = "tokenizer.json";         Url = "https://huggingface.co/BAAI/bge-reranker-v2-m3/resolve/main/tokenizer.json";                     MinBytes = 15000000 },
        @{ Name = "sentencepiece.bpe.model"; Url = "https://huggingface.co/BAAI/bge-reranker-v2-m3/resolve/main/sentencepiece.bpe.model";           MinBytes = 4500000 },
        @{ Name = "tokenizer_config.json";  Url = "https://huggingface.co/BAAI/bge-reranker-v2-m3/resolve/main/tokenizer_config.json";             MinBytes = 500 },
        @{ Name = "special_tokens_map.json"; Url = "https://huggingface.co/BAAI/bge-reranker-v2-m3/resolve/main/special_tokens_map.json";            MinBytes = 500 },
        @{ Name = "config.json";             Url = "https://huggingface.co/BAAI/bge-reranker-v2-m3/resolve/main/config.json";                        MinBytes = 300 }
    )
} else {
    $artifacts = @(
        @{ Name = "model.onnx";              Url = "https://huggingface.co/onnx-community/bge-reranker-v2-m3-ONNX/resolve/main/onnx/model_int8.onnx"; MinBytes = 500000000 },
        @{ Name = "tokenizer.json";         Url = "https://huggingface.co/onnx-community/bge-reranker-v2-m3-ONNX/resolve/main/tokenizer.json";       MinBytes = 15000000 },
        @{ Name = "sentencepiece.bpe.model"; Url = "https://huggingface.co/BAAI/bge-reranker-v2-m3/resolve/main/sentencepiece.bpe.model";           MinBytes = 4500000 },
        @{ Name = "tokenizer_config.json";  Url = "https://huggingface.co/onnx-community/bge-reranker-v2-m3-ONNX/resolve/main/tokenizer_config.json";  MinBytes = 500 },
        @{ Name = "special_tokens_map.json"; Url = "https://huggingface.co/onnx-community/bge-reranker-v2-m3-ONNX/resolve/main/special_tokens_map.json"; MinBytes = 500 },
        @{ Name = "config.json";             Url = "https://huggingface.co/onnx-community/bge-reranker-v2-m3-ONNX/resolve/main/config.json";             MinBytes = 300 }
    )
}

$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $true
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [System.TimeSpan]::FromMinutes(45)
$client.DefaultRequestHeaders.UserAgent.ParseAdd("Pigmentor-Bootstrapper/1.0")

$downloadedFiles = [System.Collections.Generic.List[string]]::new()

try {
    foreach ($item in $artifacts) {
        $name = $item.Name
        $url = $item.Url
        $minBytes = $item.MinBytes
        $destPath = Join-Path $resolvedDir $name
        $tempPath = "$destPath.tmp"

        if (-not $Force -and (Test-Path $destPath)) {
            $existingSize = (Get-Item $destPath).Length
            if ($existingSize -ge $minBytes) {
                Write-Host "[SKIP] $name already exists ($([Math]::Round($existingSize / 1MB, 2)) MB)" -ForegroundColor Green
                $downloadedFiles.Add($destPath)
                continue
            } else {
                Write-Warning "$name exists but size ($existingSize bytes) is less than expected minimum ($minBytes bytes). Re-downloading."
            }
        }

        Write-Host "[DOWNLOADING] $name from $url..." -ForegroundColor Yellow
        if (Test-Path $tempPath) {
            Remove-Item $tempPath -Force -ErrorAction SilentlyContinue
        }

        $response = $client.GetAsync($url, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "Failed to download $name. Server responded with HTTP $([int]$response.StatusCode) $($response.ReasonPhrase)"
        }

        $totalLength = $response.Content.Headers.ContentLength
        $readStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $writeStream = [System.IO.File]::Create($tempPath)

        $buffer = New-Object byte[] (1024 * 1024)
        $bytesRead = 0
        $totalRead = 0
        $lastReport = [System.DateTime]::MinValue

        while (($bytesRead = $readStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $writeStream.Write($buffer, 0, $bytesRead)
            $totalRead += $bytesRead

            if (([System.DateTime]::UtcNow - $lastReport).TotalSeconds -ge 3 -or $totalRead -eq $totalLength) {
                $lastReport = [System.DateTime]::UtcNow
                if ($totalLength) {
                    $pct = [Math]::Round(($totalRead / $totalLength) * 100, 1)
                    $mbRead = [Math]::Round($totalRead / 1MB, 1)
                    $mbTotal = [Math]::Round($totalLength / 1MB, 1)
                    Write-Host "  -> Progress: $pct% ($mbRead MB / $mbTotal MB)" -ForegroundColor DarkGray
                } else {
                    $mbRead = [Math]::Round($totalRead / 1MB, 1)
                    Write-Host "  -> Downloaded: $mbRead MB" -ForegroundColor DarkGray
                }
            }
        }

        $writeStream.Flush()
        $writeStream.Dispose()
        $readStream.Dispose()

        $finalSize = (Get-Item $tempPath).Length
        if ($finalSize -lt $minBytes) {
            Remove-Item $tempPath -Force
            throw "Downloaded $name has invalid size: $finalSize bytes (expected at least $minBytes bytes)."
        }

        Move-Item -Path $tempPath -Destination $destPath -Force
        Write-Host "[SUCCESS] Verified $name ($([Math]::Round($finalSize / 1MB, 2)) MB)" -ForegroundColor Green
        $downloadedFiles.Add($destPath)
    }

    Write-Host ""
    Write-Host "==========================================" -ForegroundColor Green
    Write-Host " BGE-Reranker-v2-m3 download complete!" -ForegroundColor Green
    Write-Host " Assets verified in: $resolvedDir" -ForegroundColor Green
    Write-Host " Enable with: pigmentor config set reranker.enabled true" -ForegroundColor Green
    Write-Host "==========================================" -ForegroundColor Green
}
finally {
    $client.Dispose()
    $handler.Dispose()
}
