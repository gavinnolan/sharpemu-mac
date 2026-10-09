// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    private const BindingFlags NativeR32Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static object[]? nativeR32OriginalQuarantine;
    [NativeR32OriginalFact] public void ActualGpuProducedR32MipZeroPrivateCopyIgnoresLaterCpuPoison() => ActualNativeR32Original(false);
    [NativeR32OriginalFact] public void ActualGpuProducedR32MipZeroCpuWriteBeforeSealRefusesWithoutConsumerOrAck() => ActualNativeR32Original(true);

    [NativeR32OriginalFact] public void ActualCommittedNativeR32PrivateCopyUnknownReceiptRetainsOriginalOwners() => ActualNativeR32Original(false, unknownCopy: true);

    [NativeR32OriginalFact] public void ActualNativeR32OriginalMappingReplacementRefusesBeforePrivateAllocation() => ActualNativeR32Original(false, identityRefusal: "replacement");
    [NativeR32OriginalFact] public void ActualNativeR32OriginalRightsRevocationRefusesBeforePrivateAllocation() => ActualNativeR32Original(false, identityRefusal: "rights");
    [NativeR32OriginalFact] public void ActualNativeR32OriginalSessionChangeRefusesBeforePrivateAllocation() => ActualNativeR32Original(false, identityRefusal: "session");
    [NativeR32OriginalFact] public void ActualNativeR32OriginalNativeOwnerReplacementRefusesBeforePrivateAllocation() => ActualNativeR32Original(false, identityRefusal: "owner");
    [NativeR32OriginalFact] public void ActualNativeR32OriginalEncodedVersionChangeRefusesBeforePrivateAllocation() => ActualNativeR32Original(false, identityRefusal: "version");
    [NativeR32OriginalFact] public void ActualNativeR32OriginalUnorderedPresentationRefusesBeforePrivateAllocation() => ActualNativeR32Original(false, identityRefusal: "presentation");

    [NativeR32OriginalFact] public void ActualSealedNativeR32SelectedConstructionRejectsReplacedOrdinaryOwner() => ActualNativeR32Original(false, constructionRefusal: "owner");
    [NativeR32OriginalFact] public void ActualSealedNativeR32SelectedConstructionRejectsChangedProducerVersion() => ActualNativeR32Original(false, constructionRefusal: "version");
    [NativeR32OriginalFact] public void ActualSealedNativeR32SelectedConstructionRejectsUnknownPendingWriter() => ActualNativeR32Original(false, constructionRefusal: "pending");

    private void ActualNativeR32Original(bool writeBeforeSeal, bool unknownCopy = false, string? identityRefusal = null,
        string? constructionRefusal = null)
    {
        SelectedMappedSeedAssertClr(); Assert.True(RequireDevice());
        Assert.Equal("1", Environment.GetEnvironmentVariable("SHARPEMU_METAL_R32_NATIVE_ORIGINAL"));
        Assert.Equal("1", Environment.GetEnvironmentVariable("SHARPEMU_METAL_SELECTED_NATIVE_REUSE"));
        Assert.Equal(RuntimeInformation.ProcessArchitecture.ToString(), Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH"));
        Assert.Equal(RuntimeInformation.FrameworkDescription, Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_FRAMEWORK"));
        Assert.Null(nativeR32OriginalQuarantine);
        var pending = new SelectedMipQueueScope();
        NativeR32Provider? provider = null; SelectedNativeExecutionState? native = null; IDisposable? logical = null;
        var tracker = GuestImageWriteTracker.Enabled;
        var backendFields = new[] { "_factory", "_backendName", "_instance" }
            .Select(name => typeof(GuestGpu).GetField(name, Methods)!).ToArray();
        var backendValues = backendFields.Select(field => field.GetValue(null)).ToArray();
        var fatalField = typeof(GpuFatal).GetField("OnFatal", Methods)!;
        var fatal = fatalField.GetValue(null); var reports = new List<string>();
        fatalField.SetValue(null, (Action<string>)reports.Add);
        Exception? primary = null; var refused = false;
        var presentationField = Presenter.GetField("_latestPresentation", Methods)!; var oldPresentation = presentationField.GetValue(null);
        var unorderedField = Presenter.GetField("_unorderedPresentationWriteSequence", Methods)!; var oldUnordered = unorderedField.GetValue(null);
        var copyHook = Presenter.GetField("_nativeR32AfterCopyCommit", Methods)!; var oldCopyHook = copyHook.GetValue(null);
        try
        {
            Assert.Null(backendValues[2]); // Each actual native row is a fresh composition-root worker.
            GuestGpu.Register(() => (IGuestGpuBackend)Activator.CreateInstance(
                Presenter.Assembly.GetTypes().Single(type => type.Name == "MetalGuestGpuBackend"))!, MetalGpuBackend.Name);
            provider = new(output.WriteLine); GuestImageWriteTracker.Configure(true);
            native = new(provider.Memory); native.ConfigureNativeR32OriginalDevice();
            logical = (IDisposable)Presenter.GetMethod("EnterGuestQueue", Methods)!.Invoke(null, ["dcb.graphics", 4UL])!;
            var producerBindings = Enumerable.Range(0, 6).Select(kind =>
            {
                var raw = PlacementTexture(kind, provider.Addresses[kind]).Descriptor!;
                Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
                return new AgcExports.TranslatedImageBinding(descriptor, true, 0, [], ResourceDescriptor: raw,
                    Shape: new(false, false, true, false, TextureNumericClass.Float), Access: GuestImageAccess.Write);
            }).ToArray();
            // CPU bytes are deliberately unrelated to the native producer.
            var poison = new byte[0x80000]; poison.AsSpan().Fill(0xA7);
            Assert.True(provider.Memory.TryWrite(provider.Address + 0x40000, poison));
            var producers = NativeR32Snapshots(provider.Memory, producerBindings);
            Assert.Equal(6, producers.Count);
            native.DispatchNativeR32Producer(producers);
            var originals = (IDictionary)Presenter.GetField("_ordinaryStorageOriginalByAddress", Methods)!.GetValue(null)!;
            Assert.Equal(6, originals.Count);
            var sourceOriginal = originals[provider.Address]!;
            var physical = sourceOriginal.GetType().GetField("Physical", NativeR32Instance)!.GetValue(sourceOriginal)!;
            var originalMapping = (CpuMappedCopyObservation)physical.GetType().GetProperty("Mapping")!.GetValue(physical)!;
            Assert.Equal((provider.Address, 0xC0000UL, provider.Address + 0x40000, 0x80000UL),
                (originalMapping.FootprintAddress, originalMapping.FootprintByteCount, originalMapping.SourceAddress, originalMapping.SourceByteCount));
            Assert.True(provider.Memory.IsMappedCopyObservationCurrent(originalMapping));
            Assert.True(provider.Memory.IsMappedCopyWriteWatchCurrent(originalMapping));
            var originalCopies = provider.Memory.Copies.Count;
            var originalReads = provider.Memory.OrdinaryReads;
            var sourceImage = ((IDictionary)Presenter.GetField("_guestImages", Methods)!.GetValue(null)!)[provider.Address]!;
            var sourceTexture = (nint)sourceImage.GetType().GetField("Texture")!.GetValue(sourceImage)!;
            var fullMipZero = NativeR32Expected(512, 256, stride: 1);
            Assert.Equal(fullMipZero, native.ReadNativeR32Resource(sourceTexture, 512, 256));
            Assert.Equal(2, sourceImage.GetType().GetField("ContentVersion")!.GetValue(sourceImage));
            var read = R32DisjointManagedBinding();
            var readRaw = read.ResourceDescriptor!.ToArray(); readRaw[0] = checked((uint)(provider.Address >> 8));
            Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(readRaw, out var readDescriptor));
            read = read with { Descriptor = readDescriptor, ResourceDescriptor = readRaw };
            var writeRaw = readRaw.ToArray(); writeRaw[3] = 0x90911FAC;
            Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(writeRaw, out var writeDescriptor));
            var write = new AgcExports.TranslatedImageBinding(writeDescriptor, true, 1, [], ResourceDescriptor: writeRaw,
                Shape: new(false, false, true, false, TextureNumericClass.Float), Access: GuestImageAccess.Write);
            if (identityRefusal is not null)
            {
                var images = (IDictionary)Presenter.GetField("_guestImages", Methods)!.GetValue(null)!;
                var versionField = sourceImage.GetType().GetField("ContentVersion")!; var oldVersion = versionField.GetValue(sourceImage);
                var beforeAdmission = Presenter.GetField("_enqueuedGuestWorkSequence", Methods)!.GetValue(null);
                try
                {
                    switch (identityRefusal)
                    {
                        case "replacement":
                            Assert.True(provider.Views[5].Unmap(provider.Address, 0xC0000, out var preserved)); Assert.True(preserved);
                            Assert.True(provider.Views[5].TryMapReservedRange(provider.Address, 0xC0000, 0, HostPageProtection.ReadWrite, out _));
                            Assert.False(provider.Memory.IsMappedCopyObservationCurrent(originalMapping)); break;
                        case "rights":
                            Assert.True(SharpEmu.Host.MacOS.MacOSHost.CreateViewMemory().ChangeAccess(provider.Address, 0xC0000, HostPageProtection.ReadOnly));
                            Assert.False(provider.Memory.IsMappedCopyObservationCurrent(originalMapping)); break;
                        case "session": Presenter.GetMethod("ReleaseSelectedStorageMipImages", Methods)!.Invoke(null, null); break;
                        case "owner":
                            // Real original texture remains retained, but a new
                            // registry owner identity may not adopt that proof.
                            images[provider.Address] = typeof(object).GetMethod("MemberwiseClone", NativeR32Instance)!.Invoke(sourceImage, null)!; break;
                        case "version": versionField.SetValue(sourceImage, (int)oldVersion! + 1); break; // negative metadata only
                        case "presentation":
                            native.PublishNativeR32UnorderedPresentation(provider.Address);
                            unorderedField.SetValue(null, 0L); // isolate the current Presentation predicate from its sticky counter
                            break;
                        default: throw new ArgumentOutOfRangeException(nameof(identityRefusal));
                    }
                    var rejected = Assert.Throws<TargetInvocationException>(() => NativeR32Snapshots(provider.Memory, [read]));
                    Assert.IsType<NotSupportedException>(rejected.InnerException);
                    Assert.Equal(originalReads, provider.Memory.OrdinaryReads);
                    Assert.Equal(originalCopies, provider.Memory.Copies.Count);
                    Assert.Equal(beforeAdmission, Presenter.GetField("_enqueuedGuestWorkSequence", Methods)!.GetValue(null));
                    Assert.Empty((IEnumerable)Presenter.GetField("_nativeR32Snapshots", Methods)!.GetValue(null)!);
                    Assert.Equal(0, (int)Presenter.GetField("_pendingGuestWorkCount", Methods)!.GetValue(null)!);
                    Assert.Empty(((IDictionary)Presenter.GetField("_selectedStorageMipImages", Methods)!.GetValue(null)!).Values);
                    Assert.Empty(reports);
                    output.WriteLine($"native-r32-original exact-{identityRefusal}-veto; real six GPU producers/status4/fullM0bytes; no read/copy/private allocation/admission/M1/EOP; no younger mapping or owner adoption");
                    return;
                }
                finally
                {
                    images[provider.Address] = sourceImage; versionField.SetValue(sourceImage, oldVersion);
                    presentationField.SetValue(null, oldPresentation); unorderedField.SetValue(null, oldUnordered);
                }
            }
            var textures = NativeR32Snapshots(provider.Memory, [read, write]);
            Assert.Equal(2, textures.Count); Assert.Empty(textures[0].RgbaPixels);
            Assert.Equal(originalReads, provider.Memory.OrdinaryReads); // no CPU M0 fallback after native production
            Assert.Equal(originalCopies + 1, provider.Memory.Copies.Count); // ONLY the original M1 seed copy
            Assert.True(provider.Memory.IsMappedCopyObservationCurrent(originalMapping)); // same original across both arms
            var samples = Presenter.GetField("_nativeR32Samples", Methods)!.GetValue(null)!;
            object?[] lookup = [textures[0], null];
            Assert.True((bool)samples.GetType().GetMethod("TryGetValue")!.Invoke(samples, lookup)!);
            var lease = lookup[1]!;
            Assert.False((bool)lease.GetType().GetField("Sealed", NativeR32Instance)!.GetValue(lease)!);
            if (writeBeforeSeal)
            {
                Assert.True(provider.Memory.TryWrite(provider.Address + 0x40000, new byte[] { 0xDB }));
                native.SubmitNativeR32Consumer(textures);
                var acknowledgements = 0;
                Assert.True((long)Presenter.GetMethod("SubmitOrderedGuestCompletion", Methods)!.Invoke(null,
                    [(Action)(() => acknowledgements++), "native-r32-no-early-eop"])! > 0);
                var failure = Assert.Throws<TargetInvocationException>(native.DrainNativeR32);
                Assert.IsType<GpuFatalException>(failure.InnerException);
                Assert.Contains("ORIGINAL mapping/read/protected token/watch", failure.ToString());
                Assert.Equal(0, acknowledgements);
                Assert.Equal((nint)0, lease.GetType().GetField("Texture", NativeR32Instance)!.GetValue(lease));
                Assert.Empty(((IDictionary)Presenter.GetField("_selectedStorageMipImages", Methods)!.GetValue(null)!).Values);
                refused = true;
                output.WriteLine("native-r32-original before-seal-overlap; producerCBstatus=4; actual original watch refusal; no private allocation/consumer/EOP; native unknown owners quarantined");
                return;
            }
            if (unknownCopy)
            {
                var encoded = sourceOriginal.GetType().GetField("LatestEncoded", NativeR32Instance)!.GetValue(sourceOriginal)!;
                var producerCb = encoded.GetType().GetField("CommandBuffer", NativeR32Instance)!.GetValue(encoded);
                var producerTexture = encoded.GetType().GetField("Texture", NativeR32Instance)!.GetValue(encoded);
                var completedBefore = Presenter.GetField("_completedGuestWorkSequence", Methods)!.GetValue(null);
                var commits = 0;
                copyHook.SetValue(null, (Action<nint>)(command =>
                {
                    Assert.False(Monitor.IsEntered(Presenter.GetField("_gate", Methods)!.GetValue(null)!));
                    Assert.NotEqual((nint)0, command);
                    Assert.True(SharpEmu.Gpu.Metal.MetalNative.SendBool(command, Selector("retainedReferences")));
                    copyHook.SetValue(null, oldCopyHook); commits++;
                    throw new TimeoutException("Actual native copy committed; completion receipt intentionally unavailable.");
                }));
                native.SubmitNativeR32Consumer(textures);
                var failure = Assert.Throws<TargetInvocationException>(native.DrainNativeR32);
                Assert.IsType<GpuFatalException>(failure.InnerException);
                Assert.Contains("completion receipt intentionally unavailable", failure.ToString());
                Assert.Equal(1, commits);
                Assert.Equal(completedBefore, Presenter.GetField("_completedGuestWorkSequence", Methods)!.GetValue(null));
                Assert.True((bool)lease.GetType().GetField("CompletionUnknown", NativeR32Instance)!.GetValue(lease)!);
                Assert.True((bool)lease.GetType().GetField("Consumed", NativeR32Instance)!.GetValue(lease)!);
                // Simulate logical retirement only, with the REAL unknown copy
                // dependency still registered. Prune must not confuse consumed
                // queue refs with released native copy ownership.
                lock (Presenter.GetField("_gate", Methods)!.GetValue(null)!)
                {
                    sourceOriginal.GetType().GetField("LatestAccepted", NativeR32Instance)!.SetValue(sourceOriginal, null);
                    sourceOriginal.GetType().GetField("LatestEncoded", NativeR32Instance)!.SetValue(sourceOriginal, null);
                }
                Presenter.GetMethod("PruneOrdinaryStorageNativeReceipts", Methods)!.Invoke(null, null);
                Assert.Equal(producerCb, encoded.GetType().GetField("CommandBuffer", NativeR32Instance)!.GetValue(encoded));
                Assert.Equal(producerTexture, encoded.GetType().GetField("Texture", NativeR32Instance)!.GetValue(encoded));
                Assert.Throws<TargetInvocationException>(() => Presenter.GetMethod("RetireOrdinaryStorageNativeOriginals", Methods)!.Invoke(null, null));
                Assert.Equal(producerCb, encoded.GetType().GetField("CommandBuffer", NativeR32Instance)!.GetValue(encoded));
                Assert.Equal(producerTexture, encoded.GetType().GetField("Texture", NativeR32Instance)!.GetValue(encoded));
                Assert.False((bool)sourceOriginal.GetType().GetField("WatchConsumed", NativeR32Instance)!.GetValue(sourceOriginal)!);
                Assert.True(provider.Memory.IsMappedCopyWriteWatchCurrent(originalMapping));
                refused = true;
                output.WriteLine("native-r32-original unknown-private-copy; realproducerCBstatus=4; real retaining private-copy committed; no copyreceipt/completedsequence/consumer; exact native producer/originalwatch/backing quarantined after actual retire refusal");
                return;
            }
            native.DrainNativeR32(); // actual copy action waits producer AND private copy status4
            Assert.True((bool)lease.GetType().GetField("Sealed", NativeR32Instance)!.GetValue(lease)!);
            var immutable = (nint)lease.GetType().GetField("Texture", NativeR32Instance)!.GetValue(lease)!;
            Assert.NotEqual((nint)0, immutable);
            Assert.Equal(fullMipZero, native.ReadNativeR32Resource(immutable, 512, 256));
            foreach (var invalid in new[] { textures[0] with { RgbaPixels = new byte[0] },
                textures[0] with { ArrayLayers = 2 }, textures[0] with { IsStorage = true } })
            {
                var substituted = Assert.Throws<TargetInvocationException>(() => Presenter.GetMethod("CopyCapturedTextureSource", Methods)!.Invoke(null, [textures[0], invalid]));
                Assert.IsType<NotSupportedException>(substituted.InnerException);
            } // real sealed CWT association cannot be adopted by changed payload/view DTOs

            Assert.True(provider.Memory.TryWrite(provider.Address + 0x40000, poison));
            Assert.False(provider.Memory.IsMappedCopyWriteWatchCurrent(originalMapping));
            Assert.True(provider.Memory.IsMappedCopyObservationCurrent(originalMapping));
            provider.CaptureBaseline(); // full post-poison guest oracle, NOT the initial guest bytes
            native.SubmitNativeR32Consumer(textures);
            var ack = 0;
            if (constructionRefusal is not null)
            {
                var images = (IDictionary)Presenter.GetField("_guestImages", Methods)!.GetValue(null)!;
                var version = sourceImage.GetType().GetField("ContentVersion")!;
                var oldVersion = version.GetValue(sourceImage);
                try
                {
                    switch (constructionRefusal)
                    {
                        case "owner": images[provider.Address] = typeof(object).GetMethod("MemberwiseClone", NativeR32Instance)!.Invoke(sourceImage, null)!; break;
                        case "version": version.SetValue(sourceImage, 3); break;
                        case "pending":
                            Assert.True((long)Presenter.GetMethod("SubmitOrderedGuestCompletion", Methods)!.Invoke(null,
                                [(Action)(() => ack++), "native-r32-unknown-pending-writer"])! > 0); break;
                        default: throw new ArgumentOutOfRangeException(nameof(constructionRefusal));
                    }
                    var failure = Assert.Throws<TargetInvocationException>(native.DrainNativeR32);
                    Assert.IsType<GpuFatalException>(failure.InnerException);
                    Assert.Equal(0, ack);
                    Assert.Empty(((IDictionary)Presenter.GetField("_selectedStorageMipImages", Methods)!.GetValue(null)!).Values);
                    Assert.Equal(originalReads, provider.Memory.OrdinaryReads);
                    Assert.Equal(originalCopies + 1, provider.Memory.Copies.Count);
                    refused = true;
                    output.WriteLine($"native-r32-original selected-construction-{constructionRefusal}-veto; real producer/private-copy CBstatus=4; exact original source watch stale after seal; no M1 allocation/encoding/EOP; no source read/recapture");
                    return;
                }
                finally { images[provider.Address] = sourceImage; version.SetValue(sourceImage, oldVersion); }
            }
            // The cohort gate cannot infer arbitrary pending callback writes.
            // Finish the real consumer before admitting this fixture's opaque
            // label Action; no unknown-writer exception is invented for it.
            native.DrainNativeR32();
            Assert.Equal(0, ack);
            var selected = Assert.Single(((IDictionary)Presenter.GetField("_selectedStorageMipImages", Methods)!.GetValue(null)!).Values.Cast<object>());
            var outputTexture = (nint)selected.GetType().GetField("Texture")!.GetValue(selected)!;
            Assert.Equal(NativeR32Expected(256, 128, stride: 2), native.ReadNativeR32Resource(outputTexture, 256, 128));
            Assert.True((long)Presenter.GetMethod("SubmitOrderedGuestCompletion", Methods)!.Invoke(null,
                [(Action)(() => { Assert.True(provider.Memory.TryWrite(provider.Address + 0x10000, new byte[] { 19, 37, 73, 109 })); ack++; }),
                    "native-r32-real-ordered-eop"])! > 0);
            Assert.Equal(0, ack);
            native.DrainNativeR32(); Assert.Equal(1, ack);
            Assert.Equal(NativeR32Expected(256, 128, stride: 2), native.ReadNativeR32Resource(outputTexture, 256, 128));
            provider.AssertFullBaseline([19, 37, 73, 109]);
            Assert.Equal(originalReads, provider.Memory.OrdinaryReads);
            Assert.Empty(reports);
            output.WriteLine($"native-r32-original actualCLR={RuntimeInformation.ProcessArchitecture}; framework={RuntimeInformation.FrameworkDescription}; " +
                "six actual GPU-written original physical owners; producer/private-copy/consumer CBstatus=4; independent131072-M0/32768-M1-floats; RRRR; full6x786432-post-poison-guest-bytes; original mapping unchanged; CPU-source-watch-stale-after-seal; orderedEOP=1; real autorelease-pool cleanup");
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            Exception? cleanup = null;
            void Attempt(Action action) { try { action(); } catch (Exception failure) { cleanup ??= failure; } }
            Attempt(() => logical?.Dispose());
            if (!refused && native?.Quarantined != true) Attempt(() => native?.RetireNativeR32OriginalsAndImages());
            Attempt(() => native?.Dispose());
            if (refused || native?.Quarantined == true || cleanup is not null)
                nativeR32OriginalQuarantine = [provider!, native!, pending];
            else
            {
                Attempt(pending.Dispose);
                Attempt(() => provider?.Dispose());
                if (cleanup is not null) nativeR32OriginalQuarantine = [provider!, native!, pending];
            }
            Attempt(() => presentationField.SetValue(null, oldPresentation)); Attempt(() => unorderedField.SetValue(null, oldUnordered));
            Attempt(() => copyHook.SetValue(null, oldCopyHook));
            lock (typeof(GuestGpu).GetField("Gate", Methods)!.GetValue(null)!)
                for (var index = 0; index < backendFields.Length; index++)
                { var field = backendFields[index]; var value = backendValues[index]; Attempt(() => field.SetValue(null, value)); }
            Attempt(() => GuestImageWriteTracker.Configure(tracker)); Attempt(() => fatalField.SetValue(null, fatal));
            if (cleanup is not null)
            {
                if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw();
                try { primary.Data["NativeR32OriginalCleanup"] = cleanup; } catch { }
            }
        }
    }

    private static byte[] NativeR32Expected(int width, int height, int stride)
    {
        var bytes = new byte[width * height * 4];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((y * width + x) * 4),
                BitConverter.SingleToUInt32Bits((y * stride) * 512 + x * stride + .25f));
        return bytes; // independent linear equation, no GnmTiling calls or native byte-derived expected data
    }
    private static IReadOnlyList<GuestDrawTexture> NativeR32Snapshots(ICpuMemory memory, AgcExports.TranslatedImageBinding[] bindings)
    {
        var snapshots = Presenter.Assembly.GetTypes().Single(type => type.Name == "MetalTextureSnapshots");
        return (IReadOnlyList<GuestDrawTexture>)snapshots.GetMethod("CreateTextureSnapshots", Methods)!.Invoke(null,
            [new CpuContext(memory, Generation.Gen5), bindings, 0])!;
    }

    private sealed class NativeR32Provider : IDisposable
    {
        internal readonly SharedBackingViews[] Views = new SharedBackingViews[6];
        internal readonly SelectedMappedSeedHole?[] Holes = new SelectedMappedSeedHole?[6];
        internal readonly ulong[] Addresses = new ulong[6];
        internal readonly byte[][] Baseline = new byte[6][];
        internal readonly NativeR32Memory Memory;
        internal ulong Address => Addresses[5];
        internal NativeR32Provider(Action<string> trace)
        {
            Memory = new(this); var host = SharpEmu.Host.MacOS.MacOSHost.CreateViewMemory();
            try
            {
                for (var i = 0; i < 6; i++)
                {
                    Views[i] = new(host, 0xC0000); Assert.True(Views[i].IsAvailable);
                    Holes[i] = new(host, trace); Addresses[i] = Holes[i]!.Address;
                    Assert.True(Addresses[i] < 1UL << 40 && (Addresses[i] & 65535) == 0);
                    Assert.True(Views[i].TryMapReservedRange(Addresses[i], 0xC0000, 0, HostPageProtection.ReadWrite, out _));
                    var seed = Enumerable.Range(0, 0xC0000).Select(j => (byte)(j * 17 + i * 23 + 31)).ToArray();
                    Assert.True(Views[i].TryWriteBacking(Addresses[i], seed));
                }
            }
            catch (Exception primary) { try { Dispose(); } catch (Exception cleanup) { try { primary.Data["NativeR32ProviderSetupCleanup"] = cleanup; } catch { } } throw; }
        }
        internal SharedBackingViews For(ulong address, ulong count) => Views[Array.FindIndex(Addresses,
            start => address >= start && address - start <= 0xC0000 && count <= 0xC0000 - (address - start))];
        internal void CaptureBaseline()
        { for (var i = 0; i < 6; i++) { Baseline[i] = new byte[0xC0000]; Assert.True(Views[i].TryReadBacking(Addresses[i], Baseline[i])); } }
        internal void AssertFullBaseline(byte[] label)
        {
            label.CopyTo(Baseline[5], 0x10000);
            for (var i = 0; i < 6; i++) { var actual = new byte[0xC0000]; Assert.True(Views[i].TryReadBacking(Addresses[i], actual)); Assert.Equal(Baseline[i], actual); }
        }
        public void Dispose()
        {
            Exception? first = null;
            for (var i = 0; i < 6; i++)
            {
                try { if (Addresses[i] != 0) { GuestImageWriteTracker.Untrack(Addresses[i]); GuestImageWriteTracker.Untrack(Addresses[i] + 0x40000); } }
                catch (Exception failure) { first ??= failure; }
                try { Views[i]?.Dispose(); if (Views[i] is { CleanupComplete: false }) throw new IOException("Native R32 view cleanup incomplete."); }
                catch (Exception failure) { Holes[i]?.Quarantine(); first ??= failure; }
                if (Views[i] is { CleanupComplete: true })
                    try { Holes[i]?.Dispose(); } catch (Exception failure) { first ??= failure; }
            }
            if (first is not null) ExceptionDispatchInfo.Capture(first).Throw();
        }
    }
    private sealed class NativeR32Memory(NativeR32Provider provider) : ICpuMemory, ICpuMappedReadWatchSource
    {
        internal int OrdinaryReads; internal readonly List<CpuMappedCopyObservation> Copies = [];
        public CpuMappedCopyResult TryReadMappedCopy(ulong address, ulong count, ulong source, Span<byte> bytes, out CpuMappedCopyObservation? observation)
        { var result = provider.For(address, count).TryReadMappedCopy(address, count, source, bytes, out observation); if (observation is not null) Copies.Add(observation); return result; }
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong address, ulong count, ulong source, Span<byte> bytes, out CpuMappedCopyObservation? observation)
        { var result = provider.For(address, count).TryReadMappedCopyWithWriteWatch(address, count, source, bytes, out observation); if (observation is not null) Copies.Add(observation); return result; }
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation original) => provider.For(original.FootprintAddress, original.FootprintByteCount).IsMappedCopyObservationCurrent(original);
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation original) => provider.For(original.FootprintAddress, original.FootprintByteCount).IsMappedCopyWriteWatchCurrent(original);
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation original) => provider.For(original.FootprintAddress, original.FootprintByteCount).ReleaseMappedCopyWriteWatch(original);
        public bool TryRead(ulong address, Span<byte> bytes) { OrdinaryReads++; return provider.For(address, (ulong)bytes.Length).TryReadBacking(address, bytes); }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes)
        { GuestImageWriteTracker.NotifyManagedWrite(address, (ulong)bytes.Length); return provider.For(address, (ulong)bytes.Length).TryWriteBacking(address, bytes); }
    }

    private sealed partial class SelectedNativeExecutionState
    {
        internal void ConfigureNativeR32OriginalDevice()
        {
            foreach (var (name, value) in new (string, object?)[] { ("_device", Device.Value), ("_commandQueue", queue) })
            { var field = Presenter.GetField(name, Methods)!; saved.Add((field, field.GetValue(null))); field.SetValue(null, value); }
        }
        internal void PublishNativeR32UnorderedPresentation(ulong address)
        {
            var shader = new Gen5MslShader("", "never_encoded_unordered_r32_presentation", Gen5MslStage.Pixel, 0);
            var compiled = Activator.CreateInstance(Presenter.Assembly.GetTypes().Single(t => t.Name == "MetalCompiledGuestShader"), [shader])!; shaders.Add(compiled);
            Presenter.GetMethod("SubmitTranslatedDraw", Methods)!.Invoke(null,
                [compiled, Array.Empty<GuestDrawTexture>(), new[] { new GuestMemoryBuffer(address + 0x10000, new byte[4], 4, 4, false, Writable: true) },
                    1u, 1u, 0u, null, 3u, 1u, 4u, null, Array.Empty<GuestVertexBuffer>(), null]);
            Assert.NotNull(Presenter.GetField("_latestPresentation", Methods)!.GetValue(null));
        }
        internal void DispatchNativeR32Producer(IReadOnlyList<GuestDrawTexture> textures)
        {
            var kind = ImageDescriptorBinding.ForImage(new ImageResource { ResourceClass = ImageResourceClass.Storage,
                NumericClass = ImageNumericClass.Float, Dimension = ImageDimension.Dim2D, Written = true })!.Value;
            var layout = Gen5MslArgumentLayout.Build(new BindingLayout { Descriptors = [new(kind, [0, 1, 2, 3, 4, 5])] });
            var field = Assert.Single(layout.Fields.Where(f => f.FieldKind == MslArgumentFieldKind.Textures));
            var msl = $$"""
                #include <metal_stdlib>
                using namespace metal;
                struct Gen5StageResources { array<texture2d<float,access::read_write>,6> {{field.Name}} [[id({{field.FirstId}})]]; };
                kernel void native_r32_original_producer(constant Gen5StageResources& r [[buffer(0)]], uint2 p [[thread_position_in_grid]]) {
                    if (all(p < uint2(r.{{field.Name}}[0].get_width(),r.{{field.Name}}[0].get_height()))) r.{{field.Name}}[0].write(float4(0.125f),p);
                    if (all(p < uint2(r.{{field.Name}}[1].get_width(),r.{{field.Name}}[1].get_height()))) r.{{field.Name}}[1].write(float4(0.25f),p);
                    if (all(p < uint2(r.{{field.Name}}[2].get_width(),r.{{field.Name}}[2].get_height()))) r.{{field.Name}}[2].write(float4(0.375f),p);
                    if (all(p < uint2(r.{{field.Name}}[3].get_width(),r.{{field.Name}}[3].get_height()))) r.{{field.Name}}[3].write(float4(0.5f),p);
                    if (all(p < uint2(r.{{field.Name}}[4].get_width(),r.{{field.Name}}[4].get_height()))) r.{{field.Name}}[4].write(float4(0.625f),p);
                    if (all(p < uint2(512u,256u))) r.{{field.Name}}[5].write(float4(float(p.y*512u+p.x)+0.25f),p);
                }
                """;
            var shader = new Gen5MslShader(msl, "native_r32_original_producer", Gen5MslStage.Compute, 0,
                ThreadgroupSizeX: 16, ThreadgroupSizeY: 16, ArgumentLayout: layout);
            SubmitNativeR32(shader, textures, new GuestStageBindings(GuestStageKind.Compute, [], [0, 1, 2, 3, 4, 5], [], [], null, [], [], false, false, 0), 32, 16);
            Presenter.GetMethod("ReleasePreparedTextureSources", Methods)!.Invoke(null, [textures]); DrainNativeR32();
        }
        internal void SubmitNativeR32Consumer(IReadOnlyList<GuestDrawTexture> textures)
        {
            var sampled = ImageDescriptorBinding.ForImage(new ImageResource { ResourceClass = ImageResourceClass.Sampled,
                NumericClass = ImageNumericClass.Float, Dimension = ImageDimension.Dim2D })!.Value;
            var stored = ImageDescriptorBinding.ForImage(new ImageResource { ResourceClass = ImageResourceClass.Storage,
                NumericClass = ImageNumericClass.Float, Dimension = ImageDimension.Dim2D, Written = true })!.Value;
            var layout = Gen5MslArgumentLayout.Build(new BindingLayout { Descriptors = [new(sampled, [0]), new(stored, [1])] });
            var fields = layout.Fields.Where(f => f.FieldKind == MslArgumentFieldKind.Textures).ToArray();
            var input = fields.Single(f => f.Kind == sampled); var target = fields.Single(f => f.Kind == stored);
            var msl = $$"""
                #include <metal_stdlib>
                using namespace metal;
                struct Gen5StageResources {
                    array<texture2d<float>,1> {{input.Name}} [[id({{input.FirstId}})]];
                    array<texture2d<float,access::read_write>,1> {{target.Name}} [[id({{target.FirstId}})]];
                };
                kernel void native_r32_original_consumer(constant Gen5StageResources& r [[buffer(0)]], uint2 p [[thread_position_in_grid]]) {
                    if (any(p >= uint2(256u,128u))) return;
                    constexpr sampler point(coord::normalized,address::clamp_to_edge,filter::nearest);
                    float4 value=r.{{input.Name}}[0].sample(point,(float2(p*2u)+0.5f)/float2(512.0f,256.0f),metal::level(0.0f));
                    r.{{target.Name}}[0].write(float4(all(value==float4(value.x)) ? value.x : -999.0f),p);
                }
                """;
            var shader = new Gen5MslShader(msl, "native_r32_original_consumer", Gen5MslStage.Compute, 0,
                ThreadgroupSizeX: 16, ThreadgroupSizeY: 16, ArgumentLayout: layout);
            SubmitNativeR32(shader, textures, new GuestStageBindings(GuestStageKind.Compute, [], [0, 1], [], [], null, [], [], false, false, 0), 16, 8);
            Presenter.GetMethod("ReleasePreparedTextureSources", Methods)!.Invoke(null, [textures]);
        }
        private void SubmitNativeR32(Gen5MslShader shader, IReadOnlyList<GuestDrawTexture> textures, GuestStageBindings bindings, uint x, uint y)
        {
            var compiled = Activator.CreateInstance(Presenter.Assembly.GetTypes().Single(t => t.Name == "MetalCompiledGuestShader"), [shader])!; shaders.Add(compiled);
            Assert.True((long)Presenter.GetMethod("SubmitComputeDispatch", Methods)!.Invoke(null,
                [0UL, compiled, textures.ToArray(), Array.Empty<GuestMemoryBuffer>(), x, y, 1u, 0u, 0u, 0u, false, bindings])! > 0);
        }
        internal void DrainNativeR32()
        {
            try
            {
                Presenter.GetMethod("DrainGuestWorkCore", Methods)!.Invoke(null, [Device.Value, queue, 5L * System.Diagnostics.Stopwatch.Frequency]);
                var command = (nint)Presenter.GetField("_lastFlushedCommandBuffer", Methods)!.GetValue(null)!;
                Assert.NotEqual((nint)0, command);
                Presenter.GetMethod("WaitForCommittedGuestBufferWriteback", Methods)!.Invoke(null, [command]);
                Assert.Equal(4UL, ReadInteger(command, Selector("status")));
            }
            catch { unknown = true; throw; }
        }
        private readonly List<(nint Buffer, nint CommandBuffer)> nativeR32UnknownReadbacks = [];
        internal byte[] ReadNativeR32Resource(nint texture, uint width, uint height)
        {
            nint buffer = 0, command = 0; var committed = false; var completed = false; Exception? primary = null;
            try
            {
                var size = checked((nuint)(width * height * 4));
                buffer = SharpEmu.Gpu.Metal.MetalNative.SendNewBuffer(Device.Value, Selector("newBufferWithLength:options:"), size, 0);
                Assert.NotEqual((nint)0, buffer);
                var borrowed = reuseSend(queue, Selector("commandBuffer")); Assert.NotEqual((nint)0, borrowed);
                command = reuseSend(borrowed, Selector("retain")); Assert.NotEqual((nint)0, command);
                Assert.True(SharpEmu.Gpu.Metal.MetalNative.SendBool(command, Selector("retainedReferences")));
                var blit = reuseSend(command, Selector("blitCommandEncoder")); Assert.NotEqual((nint)0, blit);
                Exception? encoding = null;
                try
                {
                    SharpEmu.Gpu.Metal.MetalNative.SendCopyTextureToBuffer(blit,
                        Selector("copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:"),
                        texture, 0, 0, default, new SharpEmu.Gpu.Metal.MtlSize { Width = width, Height = height, Depth = 1 },
                        buffer, 0, width * 4, width * height * 4);
                }
                catch (Exception failure) { encoding = failure; }
                try { SendVoid(blit, Selector("endEncoding")); }
                catch (Exception cleanup) when (encoding is not null) { try { encoding.Data["R32ReadbackEncoderCleanup"] = cleanup; } catch { } }
                if (encoding is not null) ExceptionDispatchInfo.Capture(encoding).Throw();
                committed = true; SendVoid(command, Selector("commit"));
                Presenter.GetMethod("WaitForCommittedGuestBufferWriteback", Methods)!.Invoke(null, [command]);
                Assert.Equal(4UL, ReadInteger(command, Selector("status"))); completed = true;
                var bytes = new byte[checked((int)size)]; var pointer = reuseSend(buffer, Selector("contents")); Assert.NotEqual((nint)0, pointer);
                Marshal.Copy(pointer, bytes, 0, bytes.Length); return bytes;
            }
            catch (Exception failure) { primary = failure; throw; }
            finally
            {
                if (committed && !completed)
                { unknown = true; nativeR32UnknownReadbacks.Add((buffer, command)); }
                else
                {
                    Exception? cleanup = null;
                    var ownedCommand = command; command = 0; var ownedBuffer = buffer; buffer = 0;
                    try { if (ownedCommand != 0) SendVoid(ownedCommand, Selector("release")); } catch (Exception failure) { cleanup = failure; }
                    try { if (ownedBuffer != 0) SendVoid(ownedBuffer, Selector("release")); } catch (Exception failure) { cleanup ??= failure; }
                    if (cleanup is not null)
                    {
                        unknown = true;
                        if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw();
                        try { primary.Data["R32ReadbackCleanup"] = cleanup; } catch { }
                    }
                }
            }
        }
        internal void RetireNativeR32OriginalsAndImages()
        {
            try
            {
                Presenter.GetMethod("RetireOrdinaryStorageNativeOriginals", Methods)!.Invoke(null, null);
                var images = (IDictionary)Presenter.GetField("_guestImages", Methods)!.GetValue(null)!;
                Exception? first = null;
                foreach (var image in images.Values.Cast<object>().ToArray())
                {
                    var textureField = image.GetType().GetField("Texture")!; var texture = (nint)textureField.GetValue(image)!;
                    textureField.SetValue(image, (nint)0);
                    try { if (texture != 0) SendVoid(texture, Selector("release")); } catch (Exception failure) { first ??= failure; }
                }
                images.Clear();
                if (first is not null) ExceptionDispatchInfo.Capture(first).Throw();
            }
            catch { unknown = true; throw; }
        }
    }
}

internal sealed class NativeR32OriginalFactAttribute : FactAttribute
{
    public NativeR32OriginalFactAttribute()
    { if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_NATIVE_R32_ORIGINAL") != "1") Skip = "Fresh actual GPU-produced R32 original fixture requires an explicit native grant."; }
}
