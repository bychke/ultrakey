"""Converts the U^2-Net ONNX model (rembg's u2net.onnx, Apache-2.0) into UltraKey's compact model format.

  python3 convert_u2net.py u2net.onnx u2net.ukm

Format (little endian):
  magic "UKM2", int32 inputSize, int32 tensorCount, int32 outputTensor, int32 opCount, ops...
  op: int32 type, then
    1 CONV    in, out, cin, cout, k, dilation, relu, bias[cout] f32, weights float16[cout*cin*k*k]
    2 MAXPOOL in, out
    3 RESIZE  in, out, refTensor            (bilinear, half-pixel, to the spatial size of refTensor)
    4 CONCAT  out, n, in[n]
    5 ADD     a, b, out
    6 SIGMOID in, out
Weights are stored as float16 (int8 visibly changes the mask on ~2 % of pixels).
"""
import struct, sys
import numpy as np
import onnx
from onnx import numpy_helper

src, dst = sys.argv[1], sys.argv[2]
g = onnx.load(src).graph
inits = {i.name: numpy_helper.to_array(i) for i in g.initializer}
producer = {o: n for n in g.node for o in n.output}
attrs = lambda n: {a.name: onnx.helper.get_attribute_value(a) for a in n.attribute}

ids = {}
def tid(name):
    if name not in ids:
        ids[name] = len(ids)
    return ids[name]

tid(g.input[0].name)
consumers = {}
for n in g.node:
    for i in n.input:
        consumers.setdefault(i, []).append(n)


def shape_source(name):
    """Follow Concat/Unsqueeze/Gather/Slice/Cast back to the Shape node; return the tensor whose size is taken."""
    stack = [name]
    seen = set()
    while stack:
        t = stack.pop()
        if t in seen or t not in producer:
            continue
        seen.add(t)
        n = producer[t]
        if n.op_type == "Shape":
            return n.input[0]
        stack.extend(n.input)
    raise RuntimeError("no Shape for " + name)


ops = []
skip = {"Shape", "Constant", "Gather", "Unsqueeze", "Slice", "Cast"}
relu_fused = set()
for n in g.node:
    t = n.op_type
    if t in skip:
        continue
    if t == "Conv":
        a = attrs(n)
        W = inits[n.input[1]].astype(np.float32)
        b = inits[n.input[2]].astype(np.float32) if len(n.input) > 2 else np.zeros(W.shape[0], np.float32)
        assert a.get("group", 1) == 1 and list(a.get("strides", [1, 1])) == [1, 1]
        k = W.shape[2]
        dil = a.get("dilations", [1, 1])[0]
        assert list(a.get("pads", [0] * 4)) == [dil * (k // 2)] * 4, a
        out = n.output[0]
        relu = 0
        cs = consumers.get(out, [])
        if len(cs) == 1 and cs[0].op_type == "Relu":
            relu = 1
            relu_fused.add(cs[0].name or id(cs[0]))
            out = cs[0].output[0]
        q = W.reshape(W.shape[0], -1).astype(np.float16)
        ops.append(("CONV", tid(n.input[0]), tid(out), W.shape[1], W.shape[0], k, dil, relu, b, q))
    elif t == "Relu":
        if (n.name or id(n)) in relu_fused:
            continue
        raise RuntimeError("unfused Relu")
    elif t == "MaxPool":
        a = attrs(n)
        assert list(a["kernel_shape"]) == [2, 2] and list(a["strides"]) == [2, 2] and a.get("ceil_mode", 0) == 1
        ops.append(("MAXPOOL", tid(n.input[0]), tid(n.output[0])))
    elif t == "Resize":
        a = attrs(n)
        assert a["mode"] == b"linear" and a["coordinate_transformation_mode"] == b"pytorch_half_pixel"
        ops.append(("RESIZE", tid(n.input[0]), tid(n.output[0]), tid(shape_source(n.input[3]))))
    elif t == "Concat":
        if attrs(n)["axis"] == 0:
            continue  # part of a shape computation feeding Resize
        assert attrs(n)["axis"] == 1
        ops.append(("CONCAT", tid(n.output[0]), [tid(i) for i in n.input]))
    elif t == "Add":
        ops.append(("ADD", tid(n.input[0]), tid(n.input[1]), tid(n.output[0])))
    elif t == "Sigmoid":
        ops.append(("SIGMOID", tid(n.input[0]), tid(n.output[0])))
    else:
        raise RuntimeError("unsupported op " + t)

out_id = tid(g.output[0].name)
size = g.input[0].type.tensor_type.shape.dim[2].dim_value or 320
with open(dst, "wb") as f:
    f.write(b"UKM2")
    f.write(struct.pack("<iiii", size, len(ids), out_id, len(ops)))
    for op in ops:
        kind = op[0]
        if kind == "CONV":
            _, i, o, cin, cout, k, dil, relu, bias, q = op
            f.write(struct.pack("<iiiiiiii", 1, i, o, cin, cout, k, dil, relu))
            f.write(bias.astype(np.float32).tobytes()); f.write(q.astype("<f2").tobytes())
        elif kind == "MAXPOOL":
            f.write(struct.pack("<iii", 2, op[1], op[2]))
        elif kind == "RESIZE":
            f.write(struct.pack("<iiii", 3, op[1], op[2], op[3]))
        elif kind == "CONCAT":
            f.write(struct.pack("<iii", 4, op[1], len(op[2])) + struct.pack("<%di" % len(op[2]), *op[2]))
        elif kind == "ADD":
            f.write(struct.pack("<iiii", 5, op[1], op[2], op[3]))
        elif kind == "SIGMOID":
            f.write(struct.pack("<iii", 6, op[1], op[2]))
print(f"{len(ops)} ops, {len(ids)} tensors, output {out_id}")
