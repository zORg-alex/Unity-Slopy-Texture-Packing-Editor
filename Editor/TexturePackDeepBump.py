"""Local DeepBump adapter. Model: HugoTini/DeepBump (GPL-3.0).

This adapter uses its own overlapping Hann-window tiler. No source textures
are uploaded. Input/output are bottom-up RGBA8; input RGB is linear light.
"""
import argparse
import hashlib
import os
from pathlib import Path
import sys
import urllib.request

REVISION = "fad19ba87daed12b1d0410a57e74f3d79e82f78d"
MODEL_HASH = "3a2ababc5fa652b19040a0f3d639b71826368e52ad805cf70ffb0e7c96a54d67"
BASE_URL = f"https://raw.githubusercontent.com/HugoTini/DeepBump/{REVISION}/"


def model_path(root, install=False):
    model = root / "deepbump256.onnx"
    if install and not model.exists():
        temporary = model.with_suffix(".download")
        try:
            with urllib.request.urlopen(BASE_URL + model.name, timeout=120) as response:
                temporary.write_bytes(response.read())
            if hashlib.sha256(temporary.read_bytes()).hexdigest() != MODEL_HASH:
                raise ValueError("Downloaded DeepBump model checksum does not match")
            temporary.replace(model)
        finally:
            temporary.unlink(missing_ok=True)
    if not model.exists() or hashlib.sha256(model.read_bytes()).hexdigest() != MODEL_HASH:
        raise ValueError("DeepBump model is missing or modified. Remove it and run setup again.")
    if install:
        with urllib.request.urlopen(BASE_URL + "LICENSE", timeout=60) as response:
            (root / "DeepBump-LICENSE.txt").write_bytes(response.read())
    return model


def session(root, install=False):
    sys.path.insert(0, str(root / "packages"))
    import onnxruntime as ort
    ort.disable_telemetry_events()
    options = ort.SessionOptions()
    options.intra_op_num_threads = max(1, min(4, (os.cpu_count() or 2) // 2))
    options.inter_op_num_threads = 1
    return ort.InferenceSession(str(model_path(root, install)), sess_options=options,
                               providers=["CPUExecutionProvider"])


def infer(runtime, pixels, seamless):
    import numpy as np
    # Present ordinary top-down sRGB to the model, as in its original image workflow.
    linear = pixels[::-1, :, :3].astype(np.float32) / 255.0
    srgb = np.where(linear <= .0031308, linear * 12.92, 1.055 * np.power(linear, 1 / 2.4) - .055)
    gray = srgb.mean(axis=2)
    height, width = gray.shape
    stride, tile, border = 128, 256, 128
    padded_h = ((height + 2 * border - tile + stride - 1) // stride) * stride + tile
    padded_w = ((width + 2 * border - tile + stride - 1) // stride) * stride + tile
    padded = np.pad(gray, ((border, padded_h - height - border), (border, padded_w - width - border)),
                    mode="wrap" if seamless else "symmetric")
    weight = np.outer(np.hanning(tile), np.hanning(tile)).astype(np.float32)
    total = np.zeros((3, padded_h, padded_w), dtype=np.float32)
    weights = np.zeros((padded_h, padded_w), dtype=np.float32)
    input_name = runtime.get_inputs()[0].name
    for y in range(0, padded_h - tile + 1, stride):
        for x in range(0, padded_w - tile + 1, stride):
            sample = np.ascontiguousarray(padded[y:y + tile, x:x + tile][None, None], dtype=np.float32)
            prediction = runtime.run(None, {input_name: sample})[0][0]
            if prediction.shape != (3, tile, tile) or not np.isfinite(prediction).all():
                raise ValueError("DeepBump returned invalid normals")
            total[:, y:y + tile, x:x + tile] += prediction * weight
            weights[y:y + tile, x:x + tile] += weight
    crop = total[:, border:border + height, border:border + width]
    crop = crop / weights[border:border + height, border:border + width][None]
    vectors = crop * 2 - 1
    length = np.maximum(np.linalg.norm(vectors, axis=0, keepdims=True), 1e-8)
    encoded = np.clip(np.rint((vectors / length * .5 + .5) * 255), 0, 255).astype(np.uint8)
    output = np.full((height, width, 4), 255, dtype=np.uint8)
    output[:, :, :3] = encoded.transpose(1, 2, 0)[::-1]
    return output


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--install-model", action="store_true")
    parser.add_argument("--input", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--width", type=int)
    parser.add_argument("--height", type=int)
    parser.add_argument("--seamless", choices=("0", "1"), default="1")
    args = parser.parse_args()
    args.root.mkdir(parents=True, exist_ok=True)
    runtime = session(args.root, args.install_model)
    import numpy as np
    if args.install_model:
        probe = runtime.run(None, {runtime.get_inputs()[0].name: np.full((1, 1, 256, 256), .5, np.float32)})[0]
        if probe.shape != (1, 3, 256, 256) or not np.isfinite(probe).all():
            raise ValueError("DeepBump model self-test failed")
        (args.root / "python.txt").write_text(sys.executable, encoding="utf-8")
        (args.root / "ready.txt").write_text(MODEL_HASH, encoding="ascii")
        print("DeepBump ready: CPU inference self-test passed")
        return
    if not args.input or not args.output or not args.width or not args.height:
        parser.error("Inference needs input, output, width and height")
    pixels = np.fromfile(args.input, dtype=np.uint8).reshape(args.height, args.width, 4)
    output = infer(runtime, pixels, args.seamless == "1")
    output.tofile(args.output)
    print(f"DeepBump generated {args.width}x{args.height} normals")


if __name__ == "__main__":
    main()
