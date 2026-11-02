using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DunkDuel.AI;
using DunkDuel.Sim;
using DunkDuel.Sim.Session;
using NUnit.Framework;

namespace DunkDuel.Tests.Headless
{
    /// <summary>
    /// The hard static gate on the compiled DunkDuel.Sim assembly: no float/double anywhere (IL opcodes, field,
    /// method and local signatures) and no references to UnityEngine, System.Random or DateTime.
    /// Source scanning can be fooled; IL cannot.
    /// </summary>
    public class IlGateTests
    {
        private sealed class PrimitiveProbe : ISignatureTypeProvider<string, object>
        {
            public readonly List<string> Found = new List<string>();

            public string GetPrimitiveType(PrimitiveTypeCode typeCode)
            {
                if (typeCode == PrimitiveTypeCode.Single || typeCode == PrimitiveTypeCode.Double) Found.Add(typeCode.ToString());
                return typeCode.ToString();
            }

            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeDefinition(handle).Name);
            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeReference(handle).Name);
            public string GetTypeFromSpecification(MetadataReader reader, object ctx, TypeSpecificationHandle handle, byte rawTypeKind) => "spec";
            public string GetSZArrayType(string elementType) => elementType + "[]";
            public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[,]";
            public string GetByReferenceType(string elementType) => elementType + "&";
            public string GetPointerType(string elementType) => elementType + "*";
            public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;
            public string GetGenericMethodParameter(object ctx, int index) => "!!" + index;
            public string GetGenericTypeParameter(object ctx, int index) => "!" + index;
            public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
            public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
            public string GetPinnedType(string elementType) => elementType;
        }

        private static readonly HashSet<byte> FloatOpcodes = new HashSet<byte>
        {
            0x22, 0x23,             // ldc.r4, ldc.r8
            0x6B, 0x6C, 0x76,       // conv.r4, conv.r8, conv.r.un
            0x4E, 0x4F, 0x56, 0x57, // ldind.r4/r8, stind.r4/r8
            0x98, 0x99, 0xA0, 0xA1, // ldelem.r4/r8, stelem.r4/r8
        };

        [Test]
        public void SimAssemblyContainsNoFloatingPointOrForbiddenReferences()
        {
            string path = typeof(Simulation).Assembly.Location;
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            MetadataReader md = pe.GetMetadataReader();
            var problems = new List<string>();
            var probe = new PrimitiveProbe();

            foreach (TypeReferenceHandle h in md.TypeReferences)
            {
                TypeReference tr = md.GetTypeReference(h);
                string full = md.GetString(tr.Namespace) + "." + md.GetString(tr.Name);
                if (full.StartsWith("UnityEngine", StringComparison.Ordinal) || full == "System.Random" || full == "System.DateTime"
                    || full == "System.DateTimeOffset" || full == "System.Single" || full == "System.Double" || full == "System.Diagnostics.Stopwatch")
                    problems.Add("type reference " + full);
            }

            foreach (FieldDefinitionHandle h in md.FieldDefinitions)
            {
                FieldDefinition f = md.GetFieldDefinition(h);
                int before = probe.Found.Count;
                f.DecodeSignature(probe, null);
                if (probe.Found.Count > before) problems.Add("field " + md.GetString(f.Name));
            }

            foreach (MethodDefinitionHandle h in md.MethodDefinitions)
            {
                MethodDefinition m = md.GetMethodDefinition(h);
                string name = md.GetString(md.GetTypeDefinition(m.GetDeclaringType()).Name) + "." + md.GetString(m.Name);
                int before = probe.Found.Count;
                m.DecodeSignature(probe, null);
                if (probe.Found.Count > before) problems.Add("signature " + name);
                if (m.RelativeVirtualAddress == 0) continue;
                MethodBodyBlock body = pe.GetMethodBody(m.RelativeVirtualAddress);
                if (!body.LocalSignature.IsNil)
                {
                    StandaloneSignature sig = md.GetStandaloneSignature(body.LocalSignature);
                    before = probe.Found.Count;
                    sig.DecodeLocalSignature(probe, null);
                    if (probe.Found.Count > before) problems.Add("locals " + name);
                }
                ScanIl(body.GetILBytes(), name, problems);
            }
            Assert.IsEmpty(problems, string.Join("\n", problems));
        }

        /// <summary>Walks IL opcodes with operand sizes and reports floating-point instructions.</summary>
        private static void ScanIl(byte[] il, string method, List<string> problems)
        {
            int i = 0;
            while (i < il.Length)
            {
                byte op = il[i++];
                if (op == 0xFE)
                {
                    byte op2 = il[i++];
                    // Two-byte opcodes with operands: ldarg/ldarga/starg/ldloc/ldloca/stloc (u16), unaligned (u8),
                    // initobj/constrained/sizeof/ldftn/ldvirtftn (token).
                    if (op2 == 0x06 || op2 == 0x07 || op2 == 0x15 || op2 == 0x16 || op2 == 0x1C) i += 4;
                    else if (op2 >= 0x09 && op2 <= 0x0E) i += 2;
                    else if (op2 == 0x12 || op2 == 0x19) i += 1;
                    continue;
                }
                if (FloatOpcodes.Contains(op)) problems.Add("IL 0x" + op.ToString("X2") + " in " + method);
                i += OperandSize(op, il, i);
            }
        }

        private static int OperandSize(byte op, byte[] il, int at)
        {
            switch (op)
            {
                case 0x0E:
                case 0x0F:
                case 0x10:
                case 0x11:
                case 0x12:
                case 0x13: // short arg/loc forms
                case 0x1F: // ldc.i4.s
                case 0x2B:
                case 0x2C:
                case 0x2D:
                case 0x2E:
                case 0x2F:
                case 0x30:
                case 0x31:
                case 0x32:
                case 0x33:
                case 0x34:
                case 0x35:
                case 0x36:
                case 0x37: // short branches
                case 0xDE: // leave.s
                    return 1;
                case 0x20: // ldc.i4
                case 0x22: // ldc.r4
                case 0x28:
                case 0x29:
                case 0x6F:
                case 0x70:
                case 0x71:
                case 0x72:
                case 0x73:
                case 0x74:
                case 0x75:
                case 0x79:
                case 0x7B:
                case 0x7C:
                case 0x7D:
                case 0x7E:
                case 0x7F:
                case 0x80:
                case 0x81:
                case 0x8C:
                case 0x8D:
                case 0x8F:
                case 0xA3:
                case 0xA4:
                case 0xA5:
                case 0xC2:
                case 0xC6:
                case 0xD0:
                case 0x27:
                case 0x38:
                case 0x39:
                case 0x3A:
                case 0x3B:
                case 0x3C:
                case 0x3D:
                case 0x3E:
                case 0x3F:
                case 0x40:
                case 0x41:
                case 0x42:
                case 0x43:
                case 0x44: // long branches
                case 0xDD: // leave
                    return 4;
                case 0x21:
                case 0x23: // ldc.i8, ldc.r8
                    return 8;
                case 0x45: // switch
                    int n = BitConverter.ToInt32(il, at);
                    return 4 + 4 * n;
                default:
                    return 0;
            }
        }

        [Test]
        public void ResimulatingEightFramesIsFast()
        {
            // Desktop proxy for "under 1 ms on a Low-tier phone": report and bound generously.
            var setup = MatchSetup.Versus(0, 1, 5);
            var sim = new Simulation(TestData.Tables);
            GameState s = sim.CreateInitialState(setup);
            var cpu0 = new CpuPlayer(TestData.Tables, TestData.Tiers, CpuTier.Legend, 0, 1);
            var cpu1 = new CpuPlayer(TestData.Tables, TestData.Tiers, CpuTier.Legend, 1, 2);
            for (int t = 0; t < 1200; t++) s = sim.Step(s, cpu0.NextInput(s, 0), cpu1.NextInput(s, 1)).State;
            var sw = Stopwatch.StartNew();
            const int rounds = 2000;
            for (int r = 0; r < rounds; r++)
            {
                GameState x = s;
                for (int k = 0; k < 8; k++) x = sim.Step(x, FrameInput.None, new FrameInput(Buttons.Right)).State;
            }
            double microsPer8 = sw.Elapsed.TotalMilliseconds * 1000.0 / rounds;
            TestContext.WriteLine($"resimulate 8 frames: {microsPer8:F1} us on this machine");
            Assert.Less(microsPer8, 250.0, "8-frame resimulation should be far below the 1 ms phone budget on desktop");
        }
    }
}
