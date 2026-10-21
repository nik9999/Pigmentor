#!/usr/bin/env bash
# Pigmentor BGE-M3 Model Downloader for Linux
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TARGET_DIR="${1:-"${SCRIPT_DIR}/../Models/bge-m3"}"
QUANTIZED=false
FORCE=false

for arg in "$@"; do
    case "$arg" in
        --quantized|-q) QUANTIZED=true ;;
        --force|-f) FORCE=true ;;
    esac
done

mkdir -p "${TARGET_DIR}"
TARGET_DIR="$(cd "${TARGET_DIR}" && pwd)"

echo "=========================================="
echo " Pigmentor BGE-M3 Model Downloader"
echo " Target directory: ${TARGET_DIR}"
echo " Mode: $(if [ "$QUANTIZED" = true ]; then echo 'Quantized int8 (~568 MB)'; else echo 'Official BAAI FP32 (~2.26 GB)'; fi)"
echo "=========================================="

download_file() {
    local name="$1"
    local url="$2"
    local min_bytes="$3"
    local dest="${TARGET_DIR}/${name}"
    local tmp="${dest}.tmp"

    if [ "$FORCE" = false ] && [ -f "${dest}" ]; then
        local current_size
        current_size=$(wc -c < "${dest}")
        if [ "$current_size" -ge "$min_bytes" ]; then
            echo "[SKIP] ${name} already exists ($(( current_size / 1048576 )) MB)"
            return 0
        fi
    fi

    echo "[DOWNLOADING] ${name} from ${url}..."
    rm -f "${tmp}"

    if curl -fL --retry 3 --progress-bar -o "${tmp}" "${url}"; then
        local downloaded_size
        downloaded_size=$(wc -c < "${tmp}")
        if [ "$downloaded_size" -lt "$min_bytes" ]; then
            echo "ERROR: ${name} is incomplete (${downloaded_size} bytes < expected ${min_bytes})" >&2
            rm -f "${tmp}"
            return 1
        fi
        mv -f "${tmp}" "${dest}"
        echo "[OK] ${name} ($(( downloaded_size / 1048576 )) MB) downloaded successfully"
    else
        echo "ERROR: Failed to download ${name}" >&2
        rm -f "${tmp}"
        return 1
    fi
}

trap 'rm -f "${TARGET_DIR}"/*.tmp' EXIT

if [ "$QUANTIZED" = true ]; then
    download_file "model.onnx" "https://huggingface.co/onnx-community/bge-m3-ONNX/resolve/main/onnx/model_int8.onnx" 500000000
    download_file "tokenizer.json" "https://huggingface.co/onnx-community/bge-m3-ONNX/resolve/main/tokenizer.json" 15000000
    download_file "sentencepiece.bpe.model" "https://huggingface.co/BAAI/bge-m3/resolve/main/sentencepiece.bpe.model" 4500000
    download_file "tokenizer_config.json" "https://huggingface.co/onnx-community/bge-m3-ONNX/resolve/main/tokenizer_config.json" 500
    download_file "special_tokens_map.json" "https://huggingface.co/onnx-community/bge-m3-ONNX/resolve/main/special_tokens_map.json" 500
    download_file "config.json" "https://huggingface.co/onnx-community/bge-m3-ONNX/resolve/main/config.json" 300
else
    download_file "model.onnx" "https://huggingface.co/BAAI/bge-m3/resolve/main/onnx/model.onnx" 500000
    download_file "model.onnx_data" "https://huggingface.co/BAAI/bge-m3/resolve/main/onnx/model.onnx_data" 2000000000
    download_file "Constant_7_attr__value" "https://huggingface.co/BAAI/bge-m3/resolve/main/onnx/Constant_7_attr__value" 50000
    download_file "tokenizer.json" "https://huggingface.co/BAAI/bge-m3/resolve/main/onnx/tokenizer.json" 15000000
    download_file "sentencepiece.bpe.model" "https://huggingface.co/BAAI/bge-m3/resolve/main/onnx/sentencepiece.bpe.model" 4500000
    download_file "tokenizer_config.json" "https://huggingface.co/BAAI/bge-m3/resolve/main/tokenizer_config.json" 500
    download_file "special_tokens_map.json" "https://huggingface.co/BAAI/bge-m3/resolve/main/special_tokens_map.json" 500
    download_file "config.json" "https://huggingface.co/BAAI/bge-m3/resolve/main/config.json" 300
fi

echo ""
echo "All BGE-M3 model assets verified in ${TARGET_DIR}."
echo "Run 'dotnet build Pigmentor.slnx' to compile with models."

