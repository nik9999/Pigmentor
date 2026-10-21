<#
.SYNOPSIS
    Downloads official BGE-M3 ONNX model assets into Models/bge-m3/.
.DESCRIPTION
    Bootstraps the canonical local model storage for Pigmentor production execution.
    By default, downloads from the official BAAI/bge-m3 Hugging Face repository.
    Use -Quantized to download the single-file int8 model (~568 MB) instead of the FP32 model (~2.26 GB).
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$TargetDir,

    [switch]$Quantized,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    $TargetDir = Join-Path $PSScriptRoot "..\Models\bge-m3"
}

$resolvedDir = [System.IO.Path]::GetFullPath($TargetDir)
if (-not (Test-Path $resolvedDir)) {
    New-Item -ItemType Directory -Path $resolvedDir -Force | Out-Null
}

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " Pigmentor BGE-M3 Model Downloader" -ForegroundColor Cyan
Write-Host " Target directory: $resolvedDir" -ForegroundColor Cyan
Write-Host " Mode: $(if ($Quantized) { 'Quantized int8 (single-file ~568 MB)' } else { 'Official BAAI FP32 (~2.26 GB with external data)' })" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# Define artifact sets
if ($Quantized) {
    $artifacts = @(
        @{ Name = "model.onnx";              Url = "https://huggingface.co/onnx-community/bge-m3-ONNX/resolve/main/onnx/model_int8.onnx"; MinBytes = 500000000 },
        @{ Name = "tokenizer.json";         Url = "https://huggingface.co/onnx-community/bge-m3-ONNX/resolve/main/tokenizer.json";       MinBytes = 15000000 },
        @{ Name = "sentencepiece.bpe.model"; Url = "https://huggingface.co/BAAI/bge-m3/resolve/main/sentencepiece.bpe.model";            MinBytes = 4500000 },
        @{ Name = "tokenizer_config.json";  Url = "https://huggingface.co/onnx-community/bge-m3-ONNX/resolve/main/tokenizer_config.json";  MinBytes = 500 },
        @{ Name = "special_tokens_map.json"; Url = "https://huggingface.co/onnx-community/bge-m3-ONNX/resolve/main/special_tokens_map.json"; MinBytes = 500 },
        @{ Name = "config.json";             Url = "https://huggingface.co/onnx-community/bge-m3-ONNX/resolve/main/config.json";             MinBytes = 300 }
    )
} else {
    $artifacts = @(
        @{ Name = "model.onnx";              Url = "https://huggingface.co/BAAI/bge-m3/resolve/main/onnx/model.onnx";                    MinBytes = 500000 },
        @{ Name = "model.onnx_data";         Url = "https://huggingface.co/BAAI/bge-m3/resolve/main/onnx/model.onnx_data";               MinBytes = 2000000000 },
        @{ Name = "Constant_7_attr__value";  Url = "https://huggingface.co/BAAI/bge-m3/resolve/main/onnx/Constant_7_attr__value";        MinBytes = 50000 },
        @{ Name = "tokenizer.json";         Url = "https://huggingface.co/BAAI/bge-m3/resolve/main/onnx/tokenizer.json";               MinBytes = 15000000 },
        @{ Name = "sentencepiece.bpe.model"; Url = "https://huggingface.co/BAAI/bge-m3/resolve/main/onnx/sentencepiece.bpe.model";       MinBytes = 4500000 },
        @{ Name = "tokenizer_config.json";  Url = "https://huggingface.co/BAAI/bge-m3/resolve/main/onnx/tokenizer_config.json";        MinBytes = 500 },
        @{ Name = "special_tokens_map.json"; Url = "https://huggingface.co/BAAI/bge-m3/resolve/main/onnx/special_tokens_map.json";       MinBytes = 500 },
        @{ Name = "config.json";             Url = "https://huggingface.co/BAAI/bge-m3/resolve/main/onnx/config.json";                   MinBytes = 300 }
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
        $totalBytesRead = 0
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $lastReport = 0

        try {
            while (($bytesRead = $readStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $writeStream.Write($buffer, 0, $bytesRead)
                $totalBytesRead += $bytesRead

                if ($stopwatch.ElapsedMilliseconds - $lastReport -gt 3000) {
                    $mbRead = [Math]::Round($totalBytesRead / 1MB, 1)
                    $speed = [Math]::Round(($totalBytesRead / 1MB) / [Math]::Max($stopwatch.Elapsed.TotalSeconds, 0.001), 2)
                    if ($totalLength) {
                        $totalMb = [Math]::Round($totalLength / 1MB, 1)
                        $pct = [Math]::Round(($totalBytesRead / $totalLength) * 100, 1)
                        Write-Host "  -> $mbRead / $totalMb MB ($pct%) at $speed MB/s" -ForegroundColor DarkGray
                    } else {
                        Write-Host "  -> $mbRead MB downloaded at $speed MB/s" -ForegroundColor DarkGray
                    }
                    $lastReport = $stopwatch.ElapsedMilliseconds
                }
            }
        } finally {
            $writeStream.Dispose()
            $readStream.Dispose()
        }

        if ($totalBytesRead -lt $minBytes) {
            Remove-Item $tempPath -Force -ErrorAction SilentlyContinue
            throw "Downloaded $name is incomplete: received $totalBytesRead bytes, expected at least $minBytes bytes."
        }

        Move-Item -Path $tempPath -Destination $destPath -Force
        $finalSize = (Get-Item $destPath).Length
        Write-Host "[OK] $name ($([Math]::Round($finalSize / 1MB, 2)) MB) downloaded in $([Math]::Round($stopwatch.Elapsed.TotalSeconds, 1))s" -ForegroundColor Green
        $downloadedFiles.Add($destPath)
    }

    Write-Host "`nAll BGE-M3 model assets verified successfully:" -ForegroundColor Cyan
    foreach ($f in $downloadedFiles) {
        Write-Host "  $f ($([Math]::Round((Get-Item $f).Length / 1MB, 2)) MB)" -ForegroundColor Gray
    }
    Write-Host "Run 'dotnet build Pigmentor.slnx' to build with deployed model assets." -ForegroundColor Green
    exit 0
} catch {
    Write-Error "BGE-M3 download failed: $_"
    # Ensure no partial .tmp files are left behind
    Get-ChildItem -Path $resolvedDir -Filter "*.tmp" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    exit 1
} finally {
    $client.Dispose()
    $handler.Dispose()
}
