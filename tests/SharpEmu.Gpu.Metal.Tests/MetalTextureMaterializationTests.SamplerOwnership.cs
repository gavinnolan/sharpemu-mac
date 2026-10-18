// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    private static nint SamplerOwnershipCall(string name, params object?[] args)
    {
        try { return (nint)Presenter.GetMethod(name, Methods)!.Invoke(null, args)!; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }

    [Theory]
    [InlineData(0)] // nil descriptor
    [InlineData(1)] // configuration failure
    [InlineData(2)] // native state creation failure
    [InlineData(3)] // nil native state
    [InlineData(4)] // accepted state retains ownership after descriptor release
    [InlineData(5)] // descriptor release failure also releases new state
    [InlineData(6)] // configuration and descriptor cleanup failure
    [InlineData(7)] // descriptor and new-state cleanup failure
    public void SamplerOwnershipProductionCreationHelperReleasesExactlyOwnedObjects(int kind)
    {
        var events = new List<string>(); var failure = new InvalidOperationException("creation/configuration sentinel");
        var releaseFailure = new InvalidOperationException("descriptor-release sentinel");
        var stateReleaseFailure = new InvalidOperationException("state-release sentinel");
        Func<nint> descriptor = () => { events.Add("descriptor"); return kind == 0 ? 0 : 11; };
        Action<nint> configure = value => { Assert.Equal((nint)11, value); events.Add("configure"); if (kind is 1 or 6) throw failure; };
        Func<nint, nint> state = value => { Assert.Equal((nint)11, value); events.Add("state"); if (kind == 2) throw failure; return kind == 3 ? 0 : 22; };
        Action<nint> release = value =>
        {
            events.Add("release" + value);
            if (kind is 5 or 6 or 7 && value == 11) throw releaseFailure;
            if (kind == 7 && value == 22) throw stateReleaseFailure;
        };
        if (kind == 4)
            Assert.Equal((nint)22, SamplerOwnershipCall("CreateOwnedSamplerState", descriptor, configure, state, release));
        else
        {
            var caught = Assert.Throws<InvalidOperationException>(() => SamplerOwnershipCall("CreateOwnedSamplerState", descriptor, configure, state, release));
            if (kind is 1 or 2 or 6) Assert.Same(failure, caught);
            if (kind is 5 or 7) Assert.Same(releaseFailure, caught);
            if (kind == 6) Assert.Same(releaseFailure, caught.Data["Metal sampler descriptor cleanup failure"]);
            if (kind == 7) Assert.Same(stateReleaseFailure, caught.Data["Metal sampler state cleanup failure"]);
            if (kind is 0 or 3) Assert.Contains("nil", caught.Message);
        }
        var expected = kind switch
        {
            0 => new[] { "descriptor" },
            1 or 6 => ["descriptor", "configure", "release11"],
            5 or 7 => ["descriptor", "configure", "state", "release11", "release22"],
            _ => ["descriptor", "configure", "state", "release11"],
        };
        Assert.Equal(expected, events);
    }

    [Fact]
    public void SamplerOwnershipReservedMipRejectsBeforeNativeDescriptorOrDeviceAccess()
    {
        var key = new GuestSampler(0, 0, 3u << 26, 0x52584D31);
        var failure = Assert.Throws<NotSupportedException>(() => SamplerOwnershipCall("GetOrCreateSampler", nint.Zero, key));
        Assert.Contains("reserved", failure.Message);
        var cache = (IDictionary)Presenter.GetField("_samplerCache", Methods)!.GetValue(null)!;
        lock (cache) Assert.False(cache.Contains(key));
    }

    [Fact]
    public async Task SamplerOwnershipConcurrentPublicationKeepsWinnerAndReleasesOnlyLoser()
    {
        var key = new GuestSampler(0, 0, 0, 0x52584D32);
        var cache = (IDictionary)Presenter.GetField("_samplerCache", Methods)!.GetValue(null)!;
        var released = new ConcurrentDictionary<nint, int>();
        Action<nint> release = value => released.AddOrUpdate(value, 1, (_, count) => count + 1);
        using var barrier = new Barrier(2);
        Task<nint> Publish(nint value) => Task.Run(() =>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
            return SamplerOwnershipCall("PublishOwnedSamplerState", key, value, release);
        });
        try
        {
            lock (cache) Assert.False(cache.Contains(key));
            var results = await Task.WhenAll(Publish(101), Publish(202));
            Assert.Equal(results[0], results[1]); Assert.Contains(results[0], new nint[] { 101, 202 });
            var loser = results[0] == 101 ? (nint)202 : 101;
            Assert.Equal(1, released[loser]); Assert.False(released.ContainsKey(results[0]));
            lock (cache) Assert.Equal(results[0], (nint)cache[key]!);
        }
        finally
        {
            lock (cache)
            {
                if (cache.Contains(key)) { release((nint)cache[key]!); cache.Remove(key); }
            }
        }
        Assert.Equal(1, released[(nint)101]); Assert.Equal(1, released[(nint)202]);
    }

    [Fact]
    public void SamplerOwnershipInvalidPublicationCannotCacheNilOrLeakNewState()
    {
        var key = new GuestSampler(0, 0, 0, 0x52584D33); var releases = new List<nint>();
        var cache = (IDictionary)Presenter.GetField("_samplerCache", Methods)!.GetValue(null)!;
        Action<nint> release = releases.Add;
        Assert.Throws<InvalidOperationException>(() => SamplerOwnershipCall("PublishOwnedSamplerState", key, nint.Zero, release));
        Assert.Empty(releases);
        lock (cache) { Assert.False(cache.Contains(key)); cache.Add(key, nint.Zero); }
        try
        {
            Assert.Throws<InvalidOperationException>(() => SamplerOwnershipCall("PublishOwnedSamplerState", key, (nint)303, release));
            Assert.Equal(new nint[] { 303 }, releases);
            Assert.Throws<InvalidOperationException>(() => SamplerOwnershipCall("GetOrCreateSampler", nint.Zero, key));
            lock (cache) Assert.Equal(nint.Zero, (nint)cache[key]!);
        }
        finally { lock (cache) cache.Remove(key); }
    }

    [Fact]
    public void SamplerOwnershipNilWinnerFailureRetainsCauseDespiteLoserCleanupFailure()
    {
        var key = new GuestSampler(0, 0, 0, 0x52584D34);
        var cache = (IDictionary)Presenter.GetField("_samplerCache", Methods)!.GetValue(null)!;
        var cleanupFailure = new InvalidOperationException("loser-release sentinel"); var releases = new List<nint>();
        Action<nint> release = value => { releases.Add(value); throw cleanupFailure; };
        lock (cache) { Assert.False(cache.Contains(key)); cache.Add(key, nint.Zero); }
        try
        {
            var caught = Assert.Throws<InvalidOperationException>(() => SamplerOwnershipCall("PublishOwnedSamplerState", key, (nint)404, release));
            Assert.Contains("cache contains a nil", caught.Message); Assert.NotSame(cleanupFailure, caught);
            Assert.Same(cleanupFailure, caught.Data["Metal sampler state cleanup failure"]);
            Assert.Equal(new nint[] { 404 }, releases);
            lock (cache) Assert.Equal(nint.Zero, (nint)cache[key]!);
        }
        finally { lock (cache) cache.Remove(key); }
    }

    private sealed class SamplerDiagnosticDataFailure : InvalidOperationException
    {
        public override IDictionary Data => throw new InvalidOperationException("diagnostic getter sentinel");
    }

    [Fact]
    public void SamplerOwnershipThrowingDiagnosticDataCannotReplacePrimaryOrSkipCleanup()
    {
        var primary = new SamplerDiagnosticDataFailure(); var released = new List<nint>();
        Func<nint> descriptor = () => 11;
        Action<nint> configure = _ => throw primary;
        Func<nint, nint> state = _ => throw new InvalidOperationException("must not create state");
        Action<nint> release = value => { released.Add(value); throw new InvalidOperationException("cleanup sentinel"); };
        var caught = Assert.Throws<SamplerDiagnosticDataFailure>(() => SamplerOwnershipCall("CreateOwnedSamplerState", descriptor, configure, state, release));
        Assert.Same(primary, caught); Assert.Equal(new nint[] { 11 }, released);
    }
}
