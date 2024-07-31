import onnx


def load_onnx_model(onnx_file_path):
    model = onnx.load(onnx_file_path)
    return model


def contains_any_operation(model, op_types):
    for node in model.graph.node:
        if node.op_type in op_types:
            return True
    return False


def get_all_opts(model):
    op_types = set()
    for node in model.graph.node:
        op_types.add(node.op_type)

    return op_types


def is_static_shape(model):
    for input_tensor in model.graph.input:
        shape = [dim.dim_value for dim in input_tensor.type.tensor_type.shape.dim]
        if 0 in shape:
            return False
    return True


def determine_engine_settings(onnx_file_path):
    model = load_onnx_model(onnx_file_path)

    precision = check_fp_precision(model)

    if is_static_shape(model):
        engine_settings = {"use_static_shapes": True, "omit_shape_args": True}
    elif contains_any_operation(model, {"Transpose", "Reshape"}):
        engine_settings = {"use_static_shapes": True}
    else:
        engine_settings = {"use_dynamic_shapes": True}

    engine_settings["precision"] = precision

    return engine_settings


def get_data_type_name(data_type):
    return onnx.TensorProto.DataType.Name(data_type)


def check_fp_precision(model):
    fp32_count = 0
    fp16_count = 0

    for input_tensor in model.graph.input:
        dtype = input_tensor.type.tensor_type.elem_type
        if dtype == onnx.TensorProto.FLOAT:
            fp32_count += 1
        elif dtype == onnx.TensorProto.FLOAT16:
            fp16_count += 1

    for output_tensor in model.graph.output:
        dtype = output_tensor.type.tensor_type.elem_type
        if dtype == onnx.TensorProto.FLOAT:
            fp32_count += 1
        elif dtype == onnx.TensorProto.FLOAT16:
            fp16_count += 1

    for initializer in model.graph.initializer:
        dtype = initializer.data_type
        if dtype == onnx.TensorProto.FLOAT:
            fp32_count += 1
        elif dtype == onnx.TensorProto.FLOAT16:
            fp16_count += 1

    if fp16_count > 0 and fp32_count == 0:
        return "FP16"
    elif fp32_count > 0 and fp16_count == 0:
        return "FP32"
    elif fp16_count > 0 and fp32_count > 0:
        return "Mixed"
    else:
        return "Unknown"