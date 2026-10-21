# BGE-M3 Production Model Assets

This directory (`Models/bge-m3/`) is the canonical repository source for production BGE-M3 runtime assets.

## Expected Assets

For production execution, this directory contains:

- `model.onnx` — BGE-M3 ONNX computational graph / weights
- `tokenizer.json` — Hugging Face BGE-M3 tokenizer definition
- `model.onnx_data` — external weights file (required if using the official full FP32 ONNX graph)
- `Constant_7_attr__value` — external constant tensor (if using the official FP32 ONNX graph)
- `sentencepiece.bpe.model` — SentencePiece BPE vocabulary
- `tokenizer_config.json`, `special_tokens_map.json`, `config.json` — tokenizer metadata

## Automated Download

To download the official BAAI BGE-M3 ONNX model assets:

### Windows (PowerShell):
```powershell
./scripts/download-bge-m3.ps1
```

Or for single-file int8 quantized model:
```powershell
./scripts/download-bge-m3.ps1 -Quantized
```

### Linux (Bash):
```bash
chmod +x ./scripts/download-bge-m3.sh
./scripts/download-bge-m3.sh
```

## Build and Deployment

During `dotnet build` or `dotnet publish` of `Pigmentor.RAG.Cli`, files in this directory are automatically deployed to:
- Build output: `bin/<Configuration>/net10.0/Models/bge-m3/`
- Publish output: `<publish>/Models/bge-m3/`

## Git Policy

Model weights (`*.onnx`, `*.onnx_data`, `*.bpe.model`, etc.) and large tokenizer files (`*.json`) are excluded from Git via `.gitignore`.
Only `README.md` and `.gitkeep` are tracked in version control.

