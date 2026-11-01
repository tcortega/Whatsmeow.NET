package server

import (
	"fmt"

	"google.golang.org/grpc/mem"
	"google.golang.org/protobuf/proto"
)

// lenientCodec is grpc-go's proto codec with AllowPartial both ways. With the default codec, a whatsmeow
// message missing a proto2 required field fails to decode before any interceptor runs, as a bare Internal with
// no ErrorInfo; with this one it reaches the slice, which maps whatsmeow's own rejection (ARCHITECTURE §6).
// It is installed per server with grpc.ForceServerCodecV2, never registered globally.
type lenientCodec struct{}

func (lenientCodec) Marshal(v any) (mem.BufferSlice, error) {
	m, ok := v.(proto.Message)
	if !ok {
		return nil, fmt.Errorf("marshal: %T is not a proto.Message", v)
	}

	b, err := proto.MarshalOptions{AllowPartial: true}.Marshal(m)
	if err != nil {
		return nil, fmt.Errorf("marshal %T: %w", v, err)
	}

	return mem.BufferSlice{mem.SliceBuffer(b)}, nil
}

func (lenientCodec) Unmarshal(data mem.BufferSlice, v any) error {
	m, ok := v.(proto.Message)
	if !ok {
		return fmt.Errorf("unmarshal: %T is not a proto.Message", v)
	}

	buf := data.MaterializeToBuffer(mem.DefaultBufferPool())
	defer buf.Free()

	if err := (proto.UnmarshalOptions{AllowPartial: true}).Unmarshal(buf.ReadOnlyData(), m); err != nil {
		return fmt.Errorf("unmarshal %T: %w", v, err)
	}

	return nil
}

func (lenientCodec) Name() string { return "proto" }
