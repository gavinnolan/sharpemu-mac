// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Ir;

namespace SharpEmu.ShaderCompiler;

/// <summary>What a host interpolant carries of one 32-bit param channel's bits.</summary>
public enum Gen5InterpolantKind : byte
{
    Float,
    HalfLow,
    HalfHigh,
    Byte0,
    Byte1,
    Byte2,
    Byte3,
    UInt,
    Int,
}

/// <summary>A host-interpolated input: param <paramref name="Location"/>'s four channels as
/// <paramref name="Kind"/> values, interpolated like the SPI_PS_INPUT_ENA barycentric
/// <paramref name="Barycentric"/> (PERSP_SAMPLE 0 ... LINEAR_CENTROID 6).</summary>
public readonly record struct Gen5Interpolant(uint Location, Gen5InterpolantKind Kind, uint Barycentric)
{
    /// <summary>The vertex side's encoding of the param and kind (the barycentric is the pixel side's).</summary>
    public uint Encoded => (Location << 4) | (uint)Kind;

    public static (uint Location, Gen5InterpolantKind Kind) Decode(uint encoded) =>
        (encoded >> 4, (Gen5InterpolantKind)(encoded & 0xFu));
}

/// <summary>After the instruction at <paramref name="Pc"/> writes VGPR <paramref name="Register"/>,
/// it holds Scale * interpolant + Offset (clamped to [0, 1] with <paramref name="Clamp"/>),
/// the interpolant being channel <paramref name="Channel"/> of param <paramref name="Location"/>'s
/// <paramref name="Kind"/> input.</summary>
public readonly record struct Gen5InterpolationRewrite(
    uint Pc,
    uint Register,
    uint Location,
    uint Channel,
    Gen5InterpolantKind Kind,
    float Scale,
    float Offset,
    bool Clamp);

public sealed class Gen5ManualInterpolationPlan
{
    /// <summary>The input slots whose vertex values the program no longer reads: a flat input
    /// (P0, the provoking vertex) feeds whatever else they reach.</summary>
    public required uint Attributes { get; init; }

    public required IReadOnlyList<Gen5Interpolant> Interpolants { get; init; }

    public required IReadOnlyList<Gen5InterpolationRewrite> Rewrites { get; init; }
}

/// <summary>
/// A pixel program that interpolates an input by hand reads its three vertex values
/// (V_INTERP_MOV P0/P10/P20 of a custom-interpolated slot, or P1/P2 deltas) and combines them
/// with the I/J barycentrics: P0 + I (P1 - P0) + J (P2 - P0), per f16 half, byte or f32,
/// usually through V_FMA_MIX_F32 and V_MAC_F32. Such a program needs no per-vertex values when
/// every one it reads ends up, symbolically, in that one combination of a single channel
/// (scaled and offset by constants): the host interpolates the unpacked channel itself, which
/// rounds like the guest's f32 arithmetic up to barycentric ULPs, and the result replaces the
/// guest's where the combination completes.
///
/// A forward dataflow over the program's control flow tracks each VGPR as a linear form over
/// the vertex values with coefficients in 1, I and J. A value written under a narrowed EXEC
/// only stands for the lanes of that EXEC (its epoch); read elsewhere it is a lane mixture.
/// Anything else a vertex value reaches (a branch, a texture address, a product of two of them,
/// a mixture) fails the plan, and the program keeps reading per-vertex values.
/// </summary>
public static class Gen5ManualInterpolation
{
    private const int VectorCount = 256;
    private const int ScalarCount = 128;
    private const uint ProvokingVertexParameter = 2;

    private sealed class PlanFailure(string reason) : Exception(reason);

    /// <summary>The plan for a pixel program, or null with the reason it reads per-vertex values.</summary>
    /// <param name="controls">SPI_PS_INPUT_CNTL per input slot (OFFSET is the param).</param>
    /// <param name="customInterpolationMask">Slots whose P10/P20 are raw vertex values.</param>
    /// <param name="inputAddress">SPI_PS_INPUT_ADDR: the layout of the input VGPRs.</param>
    public static Gen5ManualInterpolationPlan? Analyze(
        Gen5ShaderProgram program,
        ReadOnlySpan<uint> controls,
        uint customInterpolationMask,
        uint inputAddress,
        out string reason)
    {
        var attributes = PerVertexAttributes(program);
        if (attributes == 0)
        {
            reason = "no per-vertex input";
            return null;
        }

        var locations = new uint[32];
        for (var attribute = 0; attribute < 32; attribute++)
        {
            locations[attribute] = attribute < controls.Length ? controls[attribute] & 0x1Fu : (uint)attribute;
        }

        try
        {
            var analysis = new Analysis(program, locations, attributes, customInterpolationMask, inputAddress);
            var rewrites = analysis.Run();
            var interpolants = new SortedDictionary<uint, Gen5Interpolant>();
            foreach (var rewrite in rewrites)
            {
                var interpolant = new Gen5Interpolant(rewrite.Location, rewrite.Kind, analysis.BarycentricOf(rewrite));
                if (interpolants.TryGetValue(interpolant.Encoded, out var existing) && existing != interpolant)
                {
                    throw new PlanFailure($"param {rewrite.Location} {rewrite.Kind} interpolated two ways");
                }

                interpolants[interpolant.Encoded] = interpolant;
            }

            reason = $"{rewrites.Count} interpolations";
            return new Gen5ManualInterpolationPlan
            {
                Attributes = attributes,
                Interpolants = [.. interpolants.Values],
                Rewrites = rewrites,
            };
        }
        catch (PlanFailure failure)
        {
            reason = failure.Message;
            return null;
        }
    }

    // The slots read per vertex: as Gen5PixelInputMapping.PerVertexParameterMask decides,
    // per slot rather than per param.
    private static uint PerVertexAttributes(Gen5ShaderProgram program)
    {
        uint needsVertices = 0;
        uint otherThanP0 = 0;
        foreach (var instruction in program.Instructions)
        {
            if (instruction.Control is not Gen5InterpolationControl control || control.Attribute >= 32u)
            {
                continue;
            }

            var bit = 1u << (int)control.Attribute;
            if (instruction.Opcode is "VInterpMovF32" or "VInterpP1llF16" or "VInterpP1lvF16" or "VInterpP2F16" ||
                control.Modifiers is { } modifiers && ((modifiers.AbsoluteMask | modifiers.NegateMask) & 0x6u) != 0)
            {
                needsVertices |= bit;
            }

            if (instruction.Opcode != "VInterpMovF32" || control.Parameter != ProvokingVertexParameter)
            {
                otherThanP0 |= bit;
            }
        }

        return needsVertices & otherThanP0;
    }

    // ---- symbolic values ----

    private enum Tag : byte
    {
        // Anything that is the same whether or not the program reads per-vertex values.
        Opaque,
        // A lane mixture that involves a vertex value.
        Poison,
        // Known 32-bit contents (a literal or SGPR constant moved in).
        Bits,
        // An inline constant operand, whose value is the same in f32 and f16 contexts.
        Inline,
        // One vertex's raw 32-bit param channel (V_INTERP_MOV of a custom slot, or P0).
        Raw,
        // A sum of coefficient * term.
        Linear,
    }

    // Vertex -1 is the constant term.
    private readonly record struct Basis(int Vertex, uint Location, uint Channel, Gen5InterpolantKind Kind) : IComparable<Basis>
    {
        public static readonly Basis One = new(-1, 0, 0, Gen5InterpolantKind.Float);

        public bool SameValue(Basis other) =>
            Location == other.Location && Channel == other.Channel && Kind == other.Kind;

        public int CompareTo(Basis other)
        {
            var order = Vertex.CompareTo(other.Vertex);
            if (order == 0)
            {
                order = Location.CompareTo(other.Location);
            }

            if (order == 0)
            {
                order = Channel.CompareTo(other.Channel);
            }

            return order != 0 ? order : Kind.CompareTo(other.Kind);
        }
    }

    // A coefficient: C + I * barycentric I + J * barycentric J.
    private readonly record struct Poly(double C, double I, double J)
    {
        public bool IsZero => C == 0 && I == 0 && J == 0;

        public bool HasBarycentric => I != 0 || J != 0;

        public static Poly operator +(Poly left, Poly right) => new(left.C + right.C, left.I + right.I, left.J + right.J);

        public Poly Scale(double factor) => new(C * factor, I * factor, J * factor);

        public static Poly Multiply(Poly left, Poly right)
        {
            if (left.HasBarycentric && right.HasBarycentric)
            {
                throw new PlanFailure("barycentric product");
            }

            return left.HasBarycentric ? left.Scale(right.C) : right.Scale(left.C);
        }
    }

    private sealed class Value : IEquatable<Value>
    {
        public static readonly Value Opaque = new(Tag.Opaque);
        public static readonly Value Poison = new(Tag.Poison);

        private Value(Tag tag) => Tag = tag;

        public Tag Tag { get; private init; }
        public uint Bits { get; private init; }
        public double Number { get; private init; }
        public int Vertex { get; private init; }
        public uint Location { get; private init; }
        public uint Channel { get; private init; }
        public (Basis Basis, Poly Coefficient)[] Terms { get; private init; } = [];
        public int Barycentric { get; private init; } = -1;

        public static Value OfBits(uint bits) => new(Tag.Bits) { Bits = bits };

        public static Value OfInline(double number) => new(Tag.Inline) { Number = number };

        public static Value OfRaw(int vertex, uint location, uint channel) =>
            new(Tag.Raw) { Vertex = vertex, Location = location, Channel = channel };

        public static Value Constant(double number) => OfTerms([(Basis.One, new Poly(number, 0, 0))], -1);

        public static Value OfTerms(IEnumerable<(Basis Basis, Poly Coefficient)> terms, int barycentric)
        {
            var merged = new SortedDictionary<Basis, Poly>();
            foreach (var (basis, coefficient) in terms)
            {
                merged[basis] = merged.TryGetValue(basis, out var existing) ? existing + coefficient : coefficient;
            }

            var kept = merged.Where(term => !term.Value.IsZero).Select(term => (term.Key, term.Value)).ToArray();
            return new Value(Tag.Linear)
            {
                Terms = kept,
                Barycentric = kept.Any(term => term.Value.HasBarycentric) ? barycentric : -1,
            };
        }

        // Differs when the program reads its pixel inputs per vertex or not: it reads a
        // vertex other than the provoking one or the I/J barycentrics.
        public bool Dependent => Tag switch
        {
            Tag.Poison => true,
            Tag.Raw => Vertex != 0,
            Tag.Linear => Terms.Any(term => term.Basis.Vertex > 0 || term.Coefficient.HasBarycentric),
            _ => false,
        };

        public bool IsScalar => Tag == Tag.Linear && Terms.All(term => term.Basis.Vertex < 0);

        // True for a known number without barycentrics.
        public bool TryGetConstant(out double number)
        {
            number = 0;
            if (Tag != Tag.Linear || Terms.Length > 1 ||
                Terms.Length == 1 && (Terms[0].Basis.Vertex >= 0 || Terms[0].Coefficient.HasBarycentric))
            {
                return false;
            }

            number = Terms.Length == 0 ? 0 : Terms[0].Coefficient.C;
            return true;
        }

        public bool Equals(Value? other) =>
            other is not null && Tag == other.Tag && Bits == other.Bits && Number.Equals(other.Number) &&
            Vertex == other.Vertex && Location == other.Location && Channel == other.Channel &&
            Barycentric == other.Barycentric && Terms.AsSpan().SequenceEqual(other.Terms);

        public override bool Equals(object? obj) => Equals(obj as Value);

        public override int GetHashCode() => HashCode.Combine(Tag, Bits, Vertex, Location, Channel, Terms.Length);
    }

    private sealed class State
    {
        public required Value[] Vectors;
        // The EXEC epoch each VGPR was written in (0: a full EXEC).
        public required int[] Epochs;
        public required uint?[] Scalars;
        public int Epoch;

        public State Clone() => new()
        {
            Vectors = (Value[])Vectors.Clone(),
            Epochs = (int[])Epochs.Clone(),
            Scalars = (uint?[])Scalars.Clone(),
            Epoch = Epoch,
        };
    }

    private sealed class Analysis
    {
        private readonly Gen5ShaderProgram _program;
        private readonly uint[] _locations;
        private readonly uint _attributes;
        private readonly uint _custom;
        private readonly uint _inputAddress;
        private readonly IReadOnlySet<uint> _execFullPcs;
        private readonly Dictionary<(uint Pc, uint Register), int> _barycentrics = [];
        private List<Gen5InterpolationRewrite>? _rewrites;

        public Analysis(Gen5ShaderProgram program, uint[] locations, uint attributes, uint custom, uint inputAddress)
        {
            _program = program;
            _locations = locations;
            _attributes = attributes;
            _custom = custom;
            _inputAddress = inputAddress;
            _execFullPcs = Gen5ExecFullAnalysis.Analyze(program, wave32: false);
        }

        public uint BarycentricOf(Gen5InterpolationRewrite rewrite) =>
            (uint)_barycentrics[(rewrite.Pc, rewrite.Register)];

        public List<Gen5InterpolationRewrite> Run()
        {
            var instructions = _program.Instructions;
            if (instructions.Count == 0)
            {
                throw new PlanFailure("empty program");
            }

            var indexByPc = new Dictionary<uint, int>(instructions.Count);
            for (var index = 0; index < instructions.Count; index++)
            {
                indexByPc[instructions[index].Pc] = index;
            }

            var states = new State?[instructions.Count];
            states[0] = EntryState();
            var pending = new Queue<int>();
            var queued = new bool[instructions.Count];
            pending.Enqueue(0);
            queued[0] = true;
            var budget = instructions.Count * 64;
            while (pending.TryDequeue(out var index))
            {
                queued[index] = false;
                if (--budget < 0)
                {
                    throw new PlanFailure("no fixed point");
                }

                var instruction = instructions[index];
                var output = Transfer(instruction, states[index]!);
                foreach (var successor in Successors(instruction, index, indexByPc))
                {
                    var merged = states[successor] is { } existing
                        ? Merge(existing, output, instructions[successor].Pc)
                        : output.Clone();
                    if (states[successor] is null || !SameState(states[successor]!, merged))
                    {
                        states[successor] = merged;
                        if (!queued[successor])
                        {
                            queued[successor] = true;
                            pending.Enqueue(successor);
                        }
                    }
                }
            }

            // The rewrites, from each instruction's fixed-point input.
            _rewrites = [];
            for (var index = 0; index < instructions.Count; index++)
            {
                if (states[index] is { } state)
                {
                    Transfer(instructions[index], state);
                }
            }

            return _rewrites;
        }

        private IEnumerable<int> Successors(Gen5ShaderInstruction instruction, int index, Dictionary<uint, int> indexByPc)
        {
            if (instruction.Opcode is "SSetpcB64" or "SSwappcB64" or "SRfeB64" or "SCbranchJoin" or "SSetvskip")
            {
                throw new PlanFailure($"{instruction.Opcode} at 0x{instruction.Pc:X}");
            }

            if (Gen5IrBranchResolver.IsTerminator(instruction))
            {
                yield break;
            }

            if (Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out var targetPc))
            {
                if (!indexByPc.TryGetValue(targetPc, out var target))
                {
                    throw new PlanFailure($"branch out of the program at 0x{instruction.Pc:X}");
                }

                yield return target;
                if (Gen5IrBranchResolver.IsUnconditionalBranch(instruction))
                {
                    yield break;
                }
            }

            if (index + 1 < _program.Instructions.Count)
            {
                yield return index + 1;
            }
        }

        // The input VGPRs in SPI_PS_INPUT_ADDR order: each enabled barycentric pair holds its I
        // and J; the pull model's I/W, J/W, 1/W are not interpolated by the host.
        private State EntryState()
        {
            var state = new State
            {
                Vectors = Enumerable.Repeat(Value.Opaque, VectorCount).ToArray(),
                Epochs = new int[VectorCount],
                Scalars = new uint?[ScalarCount],
            };
            var vgpr = 0;
            for (var bit = 0; bit < 7; bit++)
            {
                if ((_inputAddress & (1u << bit)) == 0)
                {
                    continue;
                }

                if (bit == 3)
                {
                    state.Vectors[vgpr] = state.Vectors[vgpr + 1] = state.Vectors[vgpr + 2] = Value.Poison;
                    vgpr += 3;
                    continue;
                }

                state.Vectors[vgpr] = Value.OfTerms([(Basis.One, new Poly(0, 1, 0))], bit);
                state.Vectors[vgpr + 1] = Value.OfTerms([(Basis.One, new Poly(0, 0, 1))], bit);
                vgpr += 2;
            }

            return state;
        }

        private static bool SameState(State left, State right)
        {
            if (left.Epoch != right.Epoch)
            {
                return false;
            }

            for (var register = 0; register < VectorCount; register++)
            {
                if (left.Epochs[register] != right.Epochs[register] || !left.Vectors[register].Equals(right.Vectors[register]))
                {
                    return false;
                }
            }

            return left.Scalars.AsSpan().SequenceEqual(right.Scalars);
        }

        // A join: a VGPR keeps its value when every path holds it for the lanes the path
        // arrives with; otherwise it is a lane mixture.
        private static State Merge(State existing, State incoming, uint pc)
        {
            var epoch = existing.Epoch == incoming.Epoch ? existing.Epoch : unchecked((int)(0x4000_0000u | pc));
            var merged = new State
            {
                Vectors = new Value[VectorCount],
                Epochs = new int[VectorCount],
                Scalars = new uint?[ScalarCount],
                Epoch = epoch,
            };
            for (var register = 0; register < VectorCount; register++)
            {
                var left = existing.Vectors[register];
                var right = incoming.Vectors[register];
                var leftEpoch = existing.Epochs[register];
                var rightEpoch = incoming.Epochs[register];
                if (left.Equals(right) && leftEpoch == rightEpoch)
                {
                    merged.Vectors[register] = left;
                    merged.Epochs[register] = leftEpoch;
                }
                else if (left.Equals(right) &&
                         (leftEpoch == 0 || leftEpoch == existing.Epoch) &&
                         (rightEpoch == 0 || rightEpoch == incoming.Epoch))
                {
                    merged.Vectors[register] = left;
                    merged.Epochs[register] = leftEpoch == 0 && rightEpoch == 0 ? 0 : epoch;
                }
                else
                {
                    merged.Vectors[register] = left.Dependent || right.Dependent ? Value.Poison : Value.Opaque;
                }
            }

            for (var register = 0; register < ScalarCount; register++)
            {
                merged.Scalars[register] = existing.Scalars[register] == incoming.Scalars[register]
                    ? existing.Scalars[register]
                    : null;
            }

            return merged;
        }

        // ---- transfer ----

        private State Transfer(Gen5ShaderInstruction instruction, State input)
        {
            var state = input.Clone();
            if (_execFullPcs.Contains(instruction.Pc))
            {
                state.Epoch = 0;
            }

            var opcode = instruction.Opcode;
            if (opcode.StartsWith("SMovrel", StringComparison.Ordinal) || opcode.StartsWith("VMovrel", StringComparison.Ordinal) ||
                opcode.StartsWith("VSwaprel", StringComparison.Ordinal))
            {
                throw new PlanFailure($"{opcode} at 0x{instruction.Pc:X}");
            }

            if (instruction.Control is Gen5InterpolationControl interpolation)
            {
                TransferInterpolation(instruction, interpolation, state);
            }
            else if (!TryTransferArithmetic(instruction, state))
            {
                TransferOther(instruction, state);
            }

            TransferScalars(instruction, state);
            if (WritesExec(instruction))
            {
                state.Epoch = (int)instruction.Pc + 1;
            }

            return state;
        }

        private Value ReadVector(State state, uint register)
        {
            var value = state.Vectors[register];
            var epoch = state.Epochs[register];
            if (value.Tag == Tag.Opaque || epoch == 0 || epoch == state.Epoch)
            {
                return value;
            }

            // Written for other lanes than the ones reading it.
            return value.Dependent ? Value.Poison : Value.Opaque;
        }

        private static void WriteVector(State state, uint register, Value value, bool maybe = false)
        {
            if (register >= VectorCount)
            {
                return;
            }

            var old = state.Vectors[register];
            if (maybe)
            {
                value = old.Dependent ? Value.Poison : Value.Opaque;
            }
            else if (state.Epoch != 0 && old.Dependent && state.Epochs[register] != state.Epoch && !old.Equals(value))
            {
                // The lanes outside EXEC keep the vertex value (unless it was written for the
                // same lanes, whose others never held it).
                value = Value.Poison;
            }

            state.Vectors[register] = value;
            state.Epochs[register] = value.Tag is Tag.Opaque or Tag.Poison ? 0 : state.Epoch;
        }

        private Value ReadOperand(State state, Gen5Operand operand) => operand.Kind switch
        {
            Gen5OperandKind.VectorRegister => ReadVector(state, operand.Value),
            Gen5OperandKind.ScalarRegister => operand.Value < ScalarCount && state.Scalars[operand.Value] is { } bits
                ? Value.OfBits(bits)
                : Value.Opaque,
            Gen5OperandKind.LiteralConstant => Value.OfBits(operand.Value),
            Gen5OperandKind.EncodedConstant => operand.Value switch
            {
                128 => Value.OfInline(0.0),
                240 => Value.OfInline(0.5),
                241 => Value.OfInline(-0.5),
                242 => Value.OfInline(1.0),
                243 => Value.OfInline(-1.0),
                244 => Value.OfInline(2.0),
                245 => Value.OfInline(-2.0),
                246 => Value.OfInline(4.0),
                247 => Value.OfInline(-4.0),
                _ => Value.Opaque,
            },
            _ => Value.Opaque,
        };

        // The value as an f32 operand.
        private static Value AsFloat(Value value) => value.Tag switch
        {
            Tag.Bits => Value.Constant(BitConverter.UInt32BitsToSingle(value.Bits)),
            Tag.Inline => Value.Constant(value.Number),
            Tag.Raw => Value.OfTerms([(new Basis(value.Vertex, value.Location, value.Channel, Gen5InterpolantKind.Float), new Poly(1, 0, 0))], -1),
            _ => value,
        };

        // One 16-bit half of the value as an f16 operand, widened.
        private static Value AsHalf(Value value, bool high) => value.Tag switch
        {
            Tag.Poison => throw new PlanFailure("lane mixture of a vertex value"),
            Tag.Bits => Value.Constant((double)BitConverter.UInt16BitsToHalf((ushort)(high ? value.Bits >> 16 : value.Bits))),
            Tag.Inline => Value.Constant(value.Number),
            Tag.Raw => Value.OfTerms(
                [(new Basis(value.Vertex, value.Location, value.Channel, high ? Gen5InterpolantKind.HalfHigh : Gen5InterpolantKind.HalfLow), new Poly(1, 0, 0))],
                -1),
            _ when value.Dependent => throw new PlanFailure("half of a computed vertex value"),
            _ => Value.Opaque,
        };

        // A conversion of the raw bits (V_CVT_F32_UBYTEn, _U32, _I32, _F16).
        private static Value Convert(Value value, Gen5InterpolantKind kind)
        {
            switch (value.Tag)
            {
                case Tag.Poison:
                    throw new PlanFailure("lane mixture of a vertex value");
                case Tag.Raw:
                    return Value.OfTerms([(new Basis(value.Vertex, value.Location, value.Channel, kind), new Poly(1, 0, 0))], -1);
                case Tag.Bits:
                case Tag.Inline:
                    var bits = value.Tag == Tag.Bits ? value.Bits : BitConverter.SingleToUInt32Bits((float)value.Number);
                    return Value.Constant(kind switch
                    {
                        Gen5InterpolantKind.HalfLow => (double)BitConverter.UInt16BitsToHalf((ushort)bits),
                        Gen5InterpolantKind.HalfHigh => (double)BitConverter.UInt16BitsToHalf((ushort)(bits >> 16)),
                        Gen5InterpolantKind.Byte0 => bits & 0xFFu,
                        Gen5InterpolantKind.Byte1 => (bits >> 8) & 0xFFu,
                        Gen5InterpolantKind.Byte2 => (bits >> 16) & 0xFFu,
                        Gen5InterpolantKind.Byte3 => bits >> 24,
                        Gen5InterpolantKind.UInt => (float)bits,
                        Gen5InterpolantKind.Int => (float)(int)bits,
                        _ => BitConverter.UInt32BitsToSingle(bits),
                    });
                default:
                    if (value.Dependent)
                    {
                        throw new PlanFailure($"{kind} conversion of a computed vertex value");
                    }

                    return Value.Opaque;
            }
        }

        private static Value Absolute(Value value)
        {
            if (value.Dependent)
            {
                throw new PlanFailure("absolute value of a vertex value");
            }

            return value.TryGetConstant(out var number) ? Value.Constant(Math.Abs(number)) : Value.Opaque;
        }

        private static Value Negate(Value value) => value.Tag == Tag.Linear
            ? Value.OfTerms(value.Terms.Select(term => (term.Basis, term.Coefficient.Scale(-1))), value.Barycentric)
            : value;

        private static int CombineBarycentric(Value left, Value right)
        {
            if (left.Barycentric >= 0 && right.Barycentric >= 0 && left.Barycentric != right.Barycentric)
            {
                throw new PlanFailure("two barycentric modes in one value");
            }

            return Math.Max(left.Barycentric, right.Barycentric);
        }

        private static void RequireLinear(params Value[] values)
        {
            if (values.Any(value => value.Tag == Tag.Poison))
            {
                throw new PlanFailure("lane mixture of a vertex value");
            }
        }

        // Opaque when any operand is, which is fine only when no vertex value is involved.
        private static bool AnyOpaque(params Value[] values)
        {
            RequireLinear(values);
            if (values.All(value => value.Tag == Tag.Linear))
            {
                return false;
            }

            if (values.Any(value => value.Dependent))
            {
                throw new PlanFailure("vertex value combined with an unknown");
            }

            return true;
        }

        private static Value Add(Value left, Value right)
        {
            if (AnyOpaque(left, right))
            {
                return Value.Opaque;
            }

            return Value.OfTerms(left.Terms.Concat(right.Terms), CombineBarycentric(left, right));
        }

        private static Value Multiply(Value left, Value right)
        {
            if (AnyOpaque(left, right))
            {
                return Value.Opaque;
            }

            if (!left.IsScalar && !right.IsScalar)
            {
                if (left.Dependent || right.Dependent)
                {
                    throw new PlanFailure("product of vertex values");
                }

                return Value.Opaque;
            }

            var (scalar, other) = left.IsScalar ? (left, right) : (right, left);
            var factor = scalar.Terms.Length == 0 ? new Poly(0, 0, 0) : scalar.Terms[0].Coefficient;
            return Value.OfTerms(
                other.Terms.Select(term => (term.Basis, Poly.Multiply(term.Coefficient, factor))),
                CombineBarycentric(left, right));
        }

        // ---- interpolation ----

        private void TransferInterpolation(Gen5ShaderInstruction instruction, Gen5InterpolationControl control, State state)
        {
            if (instruction.Destinations.Count == 0 || instruction.Destinations[0].Kind != Gen5OperandKind.VectorRegister)
            {
                throw new PlanFailure($"{instruction.Opcode} without a VGPR destination");
            }

            var destination = instruction.Destinations[0].Value;
            if (control.Attribute >= 32 || (_attributes & (1u << (int)control.Attribute)) == 0)
            {
                // The host interpolates the slot as before.
                WriteVector(state, destination, Value.Opaque);
                return;
            }

            var location = _locations[control.Attribute];
            var channel = control.Channel;
            var custom = (_custom & (1u << (int)control.Attribute)) != 0;

            // P10, P20 (raw vertex values of a custom slot, deltas otherwise) and P0.
            Value Parameter(uint parameter)
            {
                var vertex = (int)((parameter + 1) % 3);
                var raw = Value.OfRaw(vertex, location, channel);
                return custom || vertex == 0 ? raw : Add(AsFloat(raw), Negate(AsFloat(Value.OfRaw(0, location, channel))));
            }

            Value Barycentric(int index)
            {
                var value = AsFloat(ReadOperand(state, instruction.Sources[index]));
                var bit = 1u << (index + 1);
                if (control.Modifiers is { } modifiers)
                {
                    if ((modifiers.AbsoluteMask & bit) != 0)
                    {
                        value = Absolute(value);
                    }

                    if ((modifiers.NegateMask & bit) != 0)
                    {
                        value = Negate(value);
                    }
                }

                return value;
            }

            Value result;
            switch (instruction.Opcode)
            {
                case "VInterpMovF32" when control.Parameter < 3:
                    result = Parameter(control.Parameter);
                    break;
                case "VInterpP1F32":
                    result = Add(Multiply(AsFloat(Parameter(0)), Barycentric(0)), AsFloat(Parameter(2)));
                    break;
                case "VInterpP2F32":
                    result = Add(Multiply(AsFloat(Parameter(1)), Barycentric(0)), AsFloat(ReadVector(state, destination)));
                    break;
                default:
                    throw new PlanFailure($"{instruction.Opcode} of a per-vertex input");
            }

            var modifiers2 = control.Modifiers;
            Finish(instruction, state, destination, result, modifiers2?.Clamp ?? false, modifiers2?.OutputModifier ?? 0);
        }

        // Stores an arithmetic result: a completed interpolation becomes a rewrite.
        private void Finish(Gen5ShaderInstruction instruction, State state, uint destination, Value result, bool clamp, uint outputModifier)
        {
            if (outputModifier != 0)
            {
                if (result.Dependent)
                {
                    throw new PlanFailure("output modifier on a vertex value");
                }

                result = Value.Opaque;
            }

            if (Canonical(result) is { } canonical)
            {
                _barycentrics[(instruction.Pc, destination)] = result.Barycentric;
                _rewrites?.Add(new Gen5InterpolationRewrite(
                    instruction.Pc, destination, canonical.Basis.Location, canonical.Basis.Channel, canonical.Basis.Kind,
                    (float)canonical.Scale, (float)canonical.Offset, clamp));
                WriteVector(state, destination, Value.Opaque);
                return;
            }

            if (clamp)
            {
                if (result.Dependent)
                {
                    throw new PlanFailure("clamp of a partial interpolation");
                }

                result = Value.Opaque;
            }

            WriteVector(state, destination, result);
        }

        // Scale * (P0 (1 - I - J) + P1 I + P2 J) + Offset of one channel's value.
        private static (Basis Basis, double Scale, double Offset)? Canonical(Value value)
        {
            if (value.Tag != Tag.Linear || value.Barycentric < 0)
            {
                return null;
            }

            var offset = 0.0;
            Basis? basis = null;
            var coefficients = new Poly?[3];
            foreach (var (term, coefficient) in value.Terms)
            {
                if (term.Vertex < 0)
                {
                    if (coefficient.HasBarycentric)
                    {
                        return null;
                    }

                    offset = coefficient.C;
                    continue;
                }

                if (basis is { } first && !first.SameValue(term))
                {
                    return null;
                }

                basis = term;
                coefficients[term.Vertex] = coefficient;
            }

            if (basis is null || coefficients[2] is not { } p2 || coefficients[1] is not { } p1 || coefficients[0] is not { } p0)
            {
                return null;
            }

            var scale = p2.J;
            var tolerance = Math.Abs(scale) * 1e-6;
            bool Near(double actual, double expected) => Math.Abs(actual - expected) <= tolerance;
            if (scale == 0 ||
                !Near(p2.C, 0) || !Near(p2.I, 0) ||
                !Near(p1.C, 0) || !Near(p1.I, scale) || !Near(p1.J, 0) ||
                !Near(p0.C, scale) || !Near(p0.I, -scale) || !Near(p0.J, -scale))
            {
                return null;
            }

            return (basis.Value with { Vertex = 0 }, scale, offset);
        }

        // ---- arithmetic ----

        private bool TryTransferArithmetic(Gen5ShaderInstruction instruction, State state)
        {
            var opcode = instruction.Opcode;
            var count = opcode switch
            {
                "VMovB32" or "VCvtF32Ubyte0" or "VCvtF32Ubyte1" or "VCvtF32Ubyte2" or "VCvtF32Ubyte3" or
                    "VCvtF32U32" or "VCvtF32I32" or "VCvtF32F16" => 1,
                "VMulF32" or "VAddF32" or "VSubF32" or "VSubrevF32" or "VMacF32" or "VFmacF32" => 2,
                "VMadF32" or "VMadAkF32" or "VMadMkF32" or "VFmaF32" or "VFmaAkF32" or "VFmaMkF32" or "VFmaMixF32" => 3,
                _ => 0,
            };
            if (count == 0 || instruction.Sources.Count < count || instruction.Destinations.Count != 1 ||
                instruction.Destinations[0].Kind != Gen5OperandKind.VectorRegister)
            {
                return false;
            }

            uint absolute = 0, negate = 0, outputModifier = 0;
            var clamp = false;
            var highHalf = false;
            switch (instruction.Control)
            {
                case null:
                    break;
                case Gen5Vop3Control vop3 when vop3.ScalarDestination is null:
                    absolute = vop3.AbsoluteMask;
                    negate = vop3.NegateMask;
                    outputModifier = vop3.OutputModifier;
                    clamp = vop3.Clamp;
                    if (vop3.OperandSelect != 0)
                    {
                        if (opcode != "VCvtF32F16" || (vop3.OperandSelect & ~1u) != 0)
                        {
                            return false;
                        }

                        highHalf = true;
                    }

                    break;
                case Gen5SdwaControl sdwa when sdwa.DestinationSelect == 6 && sdwa.ScalarDestination is null &&
                    !sdwa.Source0SignExtend && !sdwa.Source1SignExtend && sdwa.Source1Select == 6 &&
                    (sdwa.Source0Select == 6 || opcode == "VCvtF32F16" && sdwa.Source0Select is 4 or 5):
                    absolute = sdwa.AbsoluteMask;
                    negate = sdwa.NegateMask;
                    outputModifier = sdwa.OutputModifier;
                    clamp = sdwa.Clamp;
                    highHalf = sdwa.Source0Select == 5;
                    break;
                case Gen5Vop3pControl when opcode == "VFmaMixF32":
                    break;
                default:
                    return false;
            }

            var destination = instruction.Destinations[0].Value;
            var sources = new Value[count];
            for (var index = 0; index < count; index++)
            {
                sources[index] = ReadOperand(state, instruction.Sources[index]);
            }

            if (opcode == "VMovB32")
            {
                if (absolute != 0 || negate != 0 || clamp || outputModifier != 0)
                {
                    return false;
                }

                var moved = sources[0].Tag == Tag.Inline
                    ? Value.OfBits(BitConverter.SingleToUInt32Bits((float)sources[0].Number))
                    : sources[0];
                WriteVector(state, destination, moved);
                return true;
            }

            Value Modified(Value value, int index)
            {
                if ((absolute & (1u << index)) != 0)
                {
                    value = Absolute(value);
                }

                return (negate & (1u << index)) != 0 ? Negate(value) : value;
            }

            Value result;
            if (opcode.StartsWith("VCvtF32", StringComparison.Ordinal))
            {
                var kind = opcode switch
                {
                    "VCvtF32Ubyte0" => Gen5InterpolantKind.Byte0,
                    "VCvtF32Ubyte1" => Gen5InterpolantKind.Byte1,
                    "VCvtF32Ubyte2" => Gen5InterpolantKind.Byte2,
                    "VCvtF32Ubyte3" => Gen5InterpolantKind.Byte3,
                    "VCvtF32U32" => Gen5InterpolantKind.UInt,
                    "VCvtF32I32" => Gen5InterpolantKind.Int,
                    _ => highHalf ? Gen5InterpolantKind.HalfHigh : Gen5InterpolantKind.HalfLow,
                };

                // Source modifiers apply to the f16 source of V_CVT_F32_F16 only.
                if (opcode != "VCvtF32F16" && (absolute | negate) != 0)
                {
                    return false;
                }

                result = Modified(Convert(sources[0], kind), 0);
            }
            else if (opcode == "VFmaMixF32")
            {
                var mix = (Gen5Vop3pControl)instruction.Control!;
                var operands = new Value[3];
                for (var index = 0; index < 3; index++)
                {
                    var bit = 1u << index;
                    var value = (mix.OpSelHiMask & bit) != 0
                        ? AsHalf(sources[index], (mix.OpSelMask & bit) != 0)
                        : AsFloat(sources[index]);
                    if ((mix.NegHiMask & bit) != 0)
                    {
                        value = Absolute(value);
                    }

                    operands[index] = (mix.NegLoMask & bit) != 0 ? Negate(value) : value;
                }

                result = Add(Multiply(operands[0], operands[1]), operands[2]);
                clamp = mix.Clamp;
            }
            else
            {
                var operands = sources.Select((value, index) => Modified(AsFloat(value), index)).ToArray();
                result = opcode switch
                {
                    "VMulF32" => Multiply(operands[0], operands[1]),
                    "VAddF32" => Add(operands[0], operands[1]),
                    "VSubF32" => Add(operands[0], Negate(operands[1])),
                    "VSubrevF32" => Add(operands[1], Negate(operands[0])),
                    "VMacF32" or "VFmacF32" => Add(Multiply(operands[0], operands[1]), AsFloat(ReadVector(state, destination))),
                    _ => Add(Multiply(operands[0], operands[1]), operands[2]),
                };
            }

            Finish(instruction, state, destination, result, clamp, outputModifier);
            return true;
        }

        // ---- everything else ----

        private void TransferOther(Gen5ShaderInstruction instruction, State state)
        {
            var opcode = instruction.Opcode;
            foreach (var source in instruction.Sources)
            {
                if (source.Kind == Gen5OperandKind.VectorRegister && ReadVector(state, source.Value).Dependent)
                {
                    throw new PlanFailure($"{opcode} at 0x{instruction.Pc:X} reads a vertex value");
                }
            }

            foreach (var register in ControlSourceRegisters(instruction))
            {
                if (register < VectorCount && ReadVector(state, register).Dependent)
                {
                    throw new PlanFailure($"{opcode} at 0x{instruction.Pc:X} reads a vertex value");
                }
            }

            var (start, count, maybe) = Gen5WaveLaneLocalAnalysis.WrittenVectorRange(instruction);
            if (start < 0)
            {
                return;
            }

            // A partial write keeps the rest of the register, which it therefore reads.
            if (ReadsDestination(instruction))
            {
                for (var offset = 0; offset < count + maybe; offset++)
                {
                    if ((uint)(start + offset) < VectorCount && ReadVector(state, (uint)(start + offset)).Dependent)
                    {
                        throw new PlanFailure($"{opcode} at 0x{instruction.Pc:X} keeps part of a vertex value");
                    }
                }
            }

            for (var offset = 0; offset < count; offset++)
            {
                WriteVector(state, (uint)(start + offset), Value.Opaque);
            }

            for (var offset = count; offset < count + maybe; offset++)
            {
                WriteVector(state, (uint)(start + offset), Value.Opaque, maybe: true);
            }

            if (opcode.StartsWith("VSwap", StringComparison.Ordinal))
            {
                foreach (var source in instruction.Sources.Where(source => source.Kind == Gen5OperandKind.VectorRegister))
                {
                    WriteVector(state, source.Value, Value.Opaque);
                }
            }
        }

        // The address and data VGPRs memory and image instructions read beyond their first
        // source (a few more than they use where the layout is not worth decoding exactly).
        private static IEnumerable<uint> ControlSourceRegisters(Gen5ShaderInstruction instruction)
        {
            IEnumerable<uint> Range(uint start, uint count) =>
                Enumerable.Range(0, (int)count).Select(offset => start + (uint)offset);

            var writes = instruction.Opcode.Contains("Store", StringComparison.Ordinal) ||
                instruction.Opcode.Contains("Atomic", StringComparison.Ordinal);
            switch (instruction.Control)
            {
                case Gen5ImageControl image:
                {
                    var flags = Gen5ImageOpcodes.FlagsOf(instruction.Opcode);
                    var dimension = (int)Math.Min(image.Dimension, 7u);
                    var coordinates = new[] { 1, 2, 3, 3, 2, 3, 3, 4 }[dimension] + (image.IsArray ? 1 : 0);
                    var derivatives = new[] { 1, 2, 3, 2, 1, 2, 2, 2 }[dimension] * 2;
                    var count = coordinates +
                        ((flags & Gen5ImageOp.Offset) != 0 ? 1 : 0) +
                        ((flags & Gen5ImageOp.Bias) != 0 ? 1 : 0) +
                        ((flags & Gen5ImageOp.Compare) != 0 ? 1 : 0) +
                        ((flags & (Gen5ImageOp.Derivatives | Gen5ImageOp.CoarseDerivatives)) != 0 ? derivatives : 0) +
                        ((flags & Gen5ImageOp.Lod) != 0 ? 1 : 0) +
                        ((flags & Gen5ImageOp.Clamp) != 0 ? 1 : 0) +
                        ((flags & (Gen5ImageOp.Mip | Gen5ImageOp.MsaaLoad)) != 0 ? 1 : 0);
                    var registers = Enumerable.Range(0, count).Select(image.GetAddressRegister);
                    return writes ? registers.Concat(Range(image.VectorData, 4)) : registers;
                }

                case Gen5BufferMemoryControl buffer:
                {
                    var registers = Range(buffer.VectorAddress, (buffer.IndexEnabled ? 1u : 0u) + (buffer.OffsetEnabled ? 1u : 0u));
                    return writes ? registers.Concat(Range(buffer.VectorData, Math.Max(buffer.DwordCount, 1))) : registers;
                }

                case Gen5GlobalMemoryControl global:
                {
                    var registers = Range(global.VectorAddress, 2);
                    return writes ? registers.Concat(Range(global.SourceVectorRegister, Math.Max(global.DwordCount, 1))) : registers;
                }

                case Gen5RayIntersectControl ray:
                    return Enumerable.Range(0, 12).Select(ray.GetAddressRegister);
                default:
                    return [];
            }
        }

        private static readonly string[] DestinationReadingParts =
            ["Mac", "Fmac", "Dot", "16", "Writelane", "Permlane", "Swap", "Qsad", "Interp"];

        private static bool ReadsDestination(Gen5ShaderInstruction instruction) =>
            DestinationReadingParts.Any(part => instruction.Opcode.Contains(part, StringComparison.Ordinal)) ||
            instruction.Control is Gen5SdwaControl { DestinationSelect: not 6 } ||
            instruction.Control is Gen5Vop3Control { OperandSelect: not 0 } ||
            instruction.Control is Gen5ImageControl { D16: true } or Gen5ImageControl { Tfe: true } or Gen5ImageControl { Lwe: true };

        // ---- SGPRs (constants only) and EXEC ----

        private static void TransferScalars(Gen5ShaderInstruction instruction, State state)
        {
            var opcode = instruction.Opcode;
            void Clear(uint register, uint count)
            {
                for (var offset = 0u; offset < count; offset++)
                {
                    if (register + offset < ScalarCount)
                    {
                        state.Scalars[register + offset] = null;
                    }
                }
            }

            if (opcode.StartsWith('V'))
            {
                Clear(106, 2);
            }

            switch (instruction.Control)
            {
                case Gen5Vop3Control { ScalarDestination: { } vop3Destination }:
                    Clear(vop3Destination, 2);
                    break;
                case Gen5SdwaControl { ScalarDestination: { } sdwaDestination }:
                    Clear(sdwaDestination, 2);
                    break;
            }

            uint? constant = null;
            if (opcode == "SMovB32" && instruction.Sources.Count == 1)
            {
                var source = instruction.Sources[0];
                constant = source.Kind switch
                {
                    Gen5OperandKind.LiteralConstant => source.Value,
                    Gen5OperandKind.EncodedConstant => source.Value switch
                    {
                        128 => 0u,
                        240 => 0x3F000000u,
                        241 => 0xBF000000u,
                        242 => 0x3F800000u,
                        243 => 0xBF800000u,
                        244 => 0x40000000u,
                        245 => 0xC0000000u,
                        246 => 0x40800000u,
                        247 => 0xC0800000u,
                        _ => null,
                    },
                    Gen5OperandKind.ScalarRegister when source.Value < ScalarCount => state.Scalars[source.Value],
                    _ => null,
                };
            }

            foreach (var destination in instruction.Destinations)
            {
                if (destination.Kind != Gen5OperandKind.ScalarRegister)
                {
                    continue;
                }

                Clear(destination.Value, instruction.Control is Gen5ScalarMemoryControl memory
                    ? Math.Max(memory.DestinationCount, 2u)
                    : 2u);
            }

            if (constant is { } bits && instruction.Destinations.Count == 1 &&
                instruction.Destinations[0] is { Kind: Gen5OperandKind.ScalarRegister, Value: < ScalarCount } scalar)
            {
                state.Scalars[scalar.Value] = bits;
            }
        }

        private static bool WritesExec(Gen5ShaderInstruction instruction)
        {
            var opcode = instruction.Opcode;
            if (opcode.StartsWith("VCmpx", StringComparison.Ordinal) || opcode.Contains("Saveexec", StringComparison.Ordinal) ||
                opcode.Contains("Wrexec", StringComparison.Ordinal))
            {
                return true;
            }

            if (instruction.Control is Gen5Vop3Control { ScalarDestination: >= 125 and <= 127 } or
                Gen5SdwaControl { ScalarDestination: >= 125 and <= 127 })
            {
                return true;
            }

            return instruction.Destinations.Any(destination =>
                destination.Kind == Gen5OperandKind.ScalarRegister && destination.Value is >= 125 and <= 127);
        }
    }
}
