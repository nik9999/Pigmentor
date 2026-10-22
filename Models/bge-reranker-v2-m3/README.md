# BAAI BGE-Reranker-v2-m3 ONNX Model Storage

This directory holds local runtime assets for the **BAAI/bge-reranker-v2-m3** multilingual cross-encoder model executed via ONNX Runtime on CPU.

## Files

- `model.onnx` — Quantized int8 cross-encoder ONNX computational graph (~560 MB) or official FP32 graph
- `tokenizer.json` — Hugging Face fast tokenizer definition
- `sentencepiece.bpe.model` — SentencePiece vocabulary
- `tokenizer_config.json`, `special_tokens_map.json`, `config.json` — Tokenizer metadata

## Downloading Assets

Run the bootstrapper script:

```powershell
./scripts/download-bge-reranker.ps1
```

Or on Linux / macOS:

```bash
./scripts/download-bge-reranker.sh
```

## Configuration

Enable cross-encoder reranking in Pigmentor:

```bash
pigmentor config set reranker.enabled true
pigmentor config set reranker.modelPath models/bge-reranker-v2-m3/model.onnx
pigmentor config set reranker.tokenizerPath models/bge-reranker-v2-m3/tokenizer.json
```
