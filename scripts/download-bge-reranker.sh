#!/usr/bin/env bash
# Downloads BGE-Reranker-v2-m3 ONNX model assets into Models/bge-reranker-v2-m3/.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TARGET_DIR="${1:-"$SCRIPT_DIR/../Models/bge-reranker-v2-m3"}"
mkdir -p "$TARGET_DIR"

echo "Downloading BGE-Reranker-v2-m3 into $TARGET_DIR..."

download_file() {
    local name="$1"
    local url="$2"
    local min_bytes="$3"
    local dest="$TARGET_DIR/$name"

    if [ -f "$dest" ]; then
        local size
        size=$(stat -c%s "$dest" 2>/dev/null || stat -f%z "$dest" 2>/dev/null || echo 0)
        if [ "$size" -ge "$min_bytes" ]; then
            echo "[SKIP] $name already exists ($size bytes)"
            return 0
        fi
    fi

    echo "[DOWNLOADING] $name..."
    curl -L --fail --progress-bar -o "$dest.tmp" "$url"
    local final_size
    final_size=$(stat -c%s "$dest.tmp" 2>/dev/null || stat -f%z "$dest.tmp" 2>/dev/null || echo 0)
    if [ "$final_size" -lt "$min_bytes" ]; then
        echo "Error: $name download failed, size $final_size bytes is below $min_bytes" >&2
        rm -f "$dest.tmp"
        return 1
    fi
    mv "$dest.tmp" "$dest"
    echo "[SUCCESS] $name downloaded."
}

download_file "model.onnx" "https://huggingface.co/onnx-community/bge-reranker-v2-m3-ONNX/resolve/main/onnx/model_int8.onnx" 500000000
download_file "tokenizer.json" "https://huggingface.co/onnx-community/bge-reranker-v2-m3-ONNX/resolve/main/tokenizer.json" 15000000
download_file "sentencepiece.bpe.model" "https://huggingface.co/BAAI/bge-reranker-v2-m3/resolve/main/sentencepiece.bpe.model" 4500000
download_file "tokenizer_config.json" "https://huggingface.co/onnx-community/bge-reranker-v2-m3-ONNX/resolve/main/tokenizer_config.json" 500
download_file "special_tokens_map.json" "https://huggingface.co/onnx-community/bge-reranker-v2-m3-ONNX/resolve/main/special_tokens_map.json" 500
download_file "config.json" "https://huggingface.co/onnx-community/bge-reranker-v2-m3-ONNX/resolve/main/config.json" 300

echo "BGE-Reranker-v2-m3 download complete in $TARGET_DIR."
