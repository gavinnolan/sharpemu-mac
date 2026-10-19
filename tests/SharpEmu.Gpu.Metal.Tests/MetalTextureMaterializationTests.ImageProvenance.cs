// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StorageMismatchOptInOwnsCreationWordsAndRecordsActualMutationWithoutReplacingLiveImage(bool enabled)
    {
        const ulong address = 0x71F5600000;
        uint[] originalRaw = [(uint)(address >> 8), 0x44700000, 0x007FC07F, 0x90900FAC, 0, 0x00700000, 0, 0];
        uint[] incomingRaw = [(uint)(address >> 8), 0x44700000, 0x000FC01F, 0x90900FAC, 0, 0x00700000, 0, 0];
        var origin = new GuestDrawTexture(address, 512, 512, 12, 7, [], false, IsStorage: true,
            Pitch: 512, TileMode: 9, Type: 9, Descriptor: originalRaw, SourceByteCount: 2097152,
            Shape: new ShaderImageShape(false, false, true, false, TextureNumericClass.Float), Access: GuestImageAccess.Write);
        var requested = origin with { Width = 128, Height = 64, Pitch = 128, Descriptor = incomingRaw, SourceByteCount = 65536 };
        var type = Presenter.GetNestedType("GuestImage", System.Reflection.BindingFlags.NonPublic)!;
        var image = Activator.CreateInstance(type, nonPublic: true)!;
        var gate = Presenter.GetField("_gate", Methods)!.GetValue(null)!;
        var registry = (IDictionary)Presenter.GetField("_guestImages", Methods)!.GetValue(null)!;
        var flag = Presenter.GetField("_imageProvenanceEnabled", Methods)!;
        var sequence = Presenter.GetField("_executingGuestWorkSequence", Methods)!;
        lock (gate)
        {
            var previousFlag = flag.GetValue(null); var previousSequence = sequence.GetValue(null);
            object? previousDiagnostic = null;
            Assert.False(registry.Contains(address));
            try
            {
                flag.SetValue(null, enabled); sequence.SetValue(null, 100L);
                using ((IDisposable)Presenter.GetMethod("EnterGuestQueue", Methods)!.Invoke(null, ["fixture.creation", 12UL])!)
                {
                    previousDiagnostic = Presenter.GetMethod("BeginImageDiagnosticWork", Methods)!.Invoke(null, [100L]);
                    type.GetField("DiagnosticOrigin")!.SetValue(image,
                        Presenter.GetMethod("CaptureStorageImageOrigin", Methods)!.Invoke(null, [origin]));
                }
                // The production helper copies all8 words by value, not a mutable array reference.
                Array.Clear(originalRaw);
                foreach (var (name, value) in new (string, object)[] { ("Width", 512u), ("Height", 512u),
                    ("Format", Enum.ToObject(type.GetField("Format")!.FieldType, 115u)), ("Initialized", true), ("ContentVersion", 42) })
                    type.GetField(name)!.SetValue(image, value);
                registry.Add(address, image); sequence.SetValue(null, 101L);
                using ((IDisposable)Presenter.GetMethod("EnterGuestQueue", Methods)!.Invoke(null, ["fixture.mutation", 13UL])!)
                {
                    Presenter.GetMethod("BeginImageDiagnosticWork", Methods)!.Invoke(null, [101L]);
                    Presenter.GetMethod("RecordStorageImageWrites", Methods)!.Invoke(null, [new[] { origin }]);
                }
                var failure = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
                    Presenter.GetMethod("EnsureStorageImage", Methods)!.Invoke(null, [nint.Zero, requested]));
                var cause = Assert.IsType<NotSupportedException>(failure.InnerException);
                Assert.Contains("observed_width=512 observed_height=512 observed_format=115", cause.Message);
                Assert.Contains("requested_width=128 requested_height=64 requested_format=115", cause.Message);
                Assert.Same(image, registry[address]); Assert.Equal(43, type.GetField("ContentVersion")!.GetValue(image));
                if (enabled)
                {
                    // A nested diagnostic work scope restores the exact outer receipt.
                    var prior = Presenter.GetMethod("BeginImageDiagnosticWork", Methods)!.Invoke(null, [600L]);
                    var nested = Presenter.GetMethod("CaptureStorageImageOrigin", Methods)!.Invoke(null, [requested])!;
                    var nestedWork = nested.GetType().GetProperty("Work")!.GetValue(nested)!;
                    Assert.Equal(600L, nestedWork.GetType().GetProperty("Sequence")!.GetValue(nestedWork));
                    Presenter.GetMethod("EndImageDiagnosticWork", Methods)!.Invoke(null, [prior]);
                    var restored = Presenter.GetMethod("CaptureStorageImageOrigin", Methods)!.Invoke(null, [requested])!;
                    var restoredWork = restored.GetType().GetProperty("Work")!.GetValue(restored)!;
                    Assert.Equal(101L, restoredWork.GetType().GetProperty("Sequence")!.GetValue(restoredWork));
                    Assert.Contains("diagnostic_creation_kind=storage creation_sequence=100 creation_queue='fixture.creation' creation_submission=12", cause.Message);
                    Assert.Contains("creation_pitch=512 creation_tile=9", cause.Message);
                    Assert.Contains("creation_raw=[71F56000,44700000,007FC07F,90900FAC,00000000,00700000,00000000,00000000]", cause.Message);
                    Assert.Contains("mutation_kind=storage-dispatch mutation_sequence=101 mutation_queue='fixture.mutation' mutation_submission=13", cause.Message);
                    Assert.Contains("live_initialized=True live_gpu_written=True live_content_version=43", cause.Message);
                    Assert.Contains("incoming_pitch=128 incoming_tile=9", cause.Message);
                    Assert.Contains("incoming_raw=[71F56000,44700000,000FC01F,90900FAC,00000000,00700000,00000000,00000000]", cause.Message);
                }
                else Assert.DoesNotContain("diagnostic_creation_kind", cause.Message);
                // Diagnostic enablement also leaves the exact matching native view reusable.
                var matching = requested with { Width = 512, Height = 512, Pitch = 512 };
                Assert.Same(image, Presenter.GetMethod("EnsureStorageImage", Methods)!.Invoke(null, [nint.Zero, matching]));
                Assert.Equal(43, type.GetField("ContentVersion")!.GetValue(image));
            }
            finally { if (previousDiagnostic is not null) Presenter.GetMethod("EndImageDiagnosticWork", Methods)!.Invoke(null, [previousDiagnostic]); registry.Remove(address); flag.SetValue(null, previousFlag); sequence.SetValue(null, previousSequence); }
        }
    }

    [Fact]
    public void OptInProvenanceDoesNotAuthorizeInvalidStorageExtentBeforeDeviceAccess()
    {
        var gate = Presenter.GetField("_gate", Methods)!.GetValue(null)!;
        var flag = Presenter.GetField("_imageProvenanceEnabled", Methods)!;
        lock (gate)
        {
            var previous = flag.GetValue(null);
            try
            {
                flag.SetValue(null, true);
                var invalid = new GuestDrawTexture(0x71F5600000, 0, 64, 12, 7, [], false, IsStorage: true);
                var failure = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
                    Presenter.GetMethod("EnsureStorageImage", Methods)!.Invoke(null, [nint.Zero, invalid]));
                Assert.Contains("unsupported extent 0x64", Assert.IsType<NotSupportedException>(failure.InnerException).Message);
            }
            finally { flag.SetValue(null, previous); }
        }
    }
}
