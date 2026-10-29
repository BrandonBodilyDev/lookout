"""Writes tests/fixtures/onnx/tiny-detector.onnx: a stand-in for a DETR-family fire detector.

It has the same interface as an RF-DETR ONNX export (one image input [1, 3, H, W]; boxes [1, N, 4]
as normalized cx, cy, w, h; class logits [1, N, C]) but ignores the image and always returns the
same two detections: one confident box in the top-left quarter and one low-score box in the
bottom-right. That lets the .NET test check OnnxFireDetector's graph handling, thresholding and
box-to-pixel mapping without downloading real weights.

    python -m venv .venv && .venv/bin/pip install onnx==1.19.1
    .venv/bin/python tools/make_test_detector.py
"""
import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper

boxes = np.array([[[0.25, 0.25, 0.20, 0.20], [0.75, 0.75, 0.10, 0.10]]], dtype=np.float32)
logits = np.array([[[3.0], [-3.0]]], dtype=np.float32)  # sigmoid: 0.95 and 0.05

graph = helper.make_graph(
    nodes=[
        helper.make_node("Identity", ["boxes_const"], ["dets"]),
        helper.make_node("Identity", ["logits_const"], ["labels"]),
    ],
    name="tiny-detector",
    inputs=[helper.make_tensor_value_info("input", TensorProto.FLOAT, [1, 3, 64, 64])],
    outputs=[
        helper.make_tensor_value_info("dets", TensorProto.FLOAT, [1, 2, 4]),
        helper.make_tensor_value_info("labels", TensorProto.FLOAT, [1, 2, 1]),
    ],
    initializer=[numpy_helper.from_array(boxes, "boxes_const"), numpy_helper.from_array(logits, "logits_const")],
)
model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)], producer_name="lookout-test")
model.ir_version = 8
onnx.checker.check_model(model)
onnx.save(model, "tests/fixtures/onnx/tiny-detector.onnx")
print("wrote tests/fixtures/onnx/tiny-detector.onnx")
