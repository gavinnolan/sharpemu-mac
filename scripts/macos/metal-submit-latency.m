// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
//
// Metal submission-latency probe: does a command buffer wait to be scheduled, and
// what makes it wait? The v2 host measured committed submissions sitting 230 to 385
// milliseconds before the driver picked them up on titles that submit rarely, and
// this is the standalone control for that, outside the emulator and without any of
// its machinery.
//
// Each case commits trivial command buffers at a fixed interval and reports, per
// buffer, the gap from -commit to kernelStartTime (the driver picking it up) and to
// the completion, plus how many completions took longer than fifty milliseconds.
//
// Build and run (the emulator is x86-64 under Rosetta, so the control is too):
//
//   clang -arch x86_64 -fobjc-arc -O2 -Wno-gnu-folding-constant \
//       -framework Foundation -framework AppKit -framework Metal -framework QuartzCore \
//       -o /tmp/metal-submit-latency scripts/macos/metal-submit-latency.m
//   arch -x86_64 /tmp/metal-submit-latency [rate|windows|presenter|guard|all]
//
// IMPORTANT: nothing else may be using the GPU. A game running in another window
// turns every case below into 350-960 ms stalls, which is what made this look like a
// driver or power-state problem in the first place.

#import <AppKit/AppKit.h>
#import <Metal/Metal.h>
#import <QuartzCore/QuartzCore.h>
#include <pthread.h>
#include <sys/mman.h>

#define SAMPLES 50
#define WARMUP 3

static int CompareDouble(const void *a, const void *b) {
    double left = *(const double *)a, right = *(const double *)b;
    return left < right ? -1 : (left > right ? 1 : 0);
}

typedef struct {
    double done[SAMPLES];
    double kernel[SAMPLES];
    int count;
} Samples;

static void Report(const char *name, Samples *samples) {
    int count = samples->count;
    if (count == 0) {
        printf("%-46s no samples\n", name);
        return;
    }

    double sorted[SAMPLES], kernel[SAMPLES];
    memcpy(sorted, samples->done, sizeof(double) * count);
    memcpy(kernel, samples->kernel, sizeof(double) * count);
    int stalls = 0;
    double total = 0;
    for (int i = 0; i < count; i++) {
        total += samples->done[i];
        if (samples->done[i] > 50.0) stalls++;
    }

    qsort(sorted, count, sizeof(double), CompareDouble);
    qsort(kernel, count, sizeof(double), CompareDouble);
    printf("%-46s commit_to_done_ms median=%7.2f p90=%8.2f max=%8.2f mean=%7.2f "
           "stalls>50ms=%2d/%d | commit_to_kernel_ms median=%5.2f max=%7.2f\n",
           name, sorted[count / 2], sorted[(count * 9) / 10], sorted[count - 1], total / count,
           stalls, count, kernel[count / 2], kernel[count - 1]);
}

// One case: `SAMPLES` command buffers, `intervalMs` apart, each doing a tiny fill
// into `target` so the buffer is not empty.
static void Measure(id<MTLDevice> device, const char *name, id<MTLBuffer> target,
                    double intervalMs, id<MTLSharedEvent> event, BOOL completionHandler) {
    if (!target) {
        printf("%-46s REFUSED\n", name);
        return;
    }

    id<MTLCommandQueue> queue = [device newCommandQueue];
    Samples samples = {.count = 0};
    static uint64_t eventValue = 0;
    for (int i = 0; i < SAMPLES + WARMUP; i++) {
        @autoreleasepool {
            id<MTLCommandBuffer> buffer = [queue commandBuffer];
            id<MTLBlitCommandEncoder> blit = [buffer blitCommandEncoder];
            [blit fillBuffer:target range:NSMakeRange(0, 256) value:(uint8_t)i];
            [blit endEncoding];
            if (event) [buffer encodeSignalEvent:event value:++eventValue];
            if (completionHandler) {
                [buffer addCompletedHandler:^(id<MTLCommandBuffer> _Nonnull done) { (void)done; }];
            }

            double committed = CACurrentMediaTime();
            [buffer commit];
            [buffer waitUntilCompleted];
            double finished = CACurrentMediaTime();
            if (i >= WARMUP) {
                samples.done[samples.count] = (finished - committed) * 1000.0;
                samples.kernel[samples.count] = (buffer.kernelStartTime - committed) * 1000.0;
                samples.count++;
            }

            double elapsed = (CACurrentMediaTime() - committed) * 1000.0;
            if (elapsed < intervalMs) usleep((useconds_t)((intervalMs - elapsed) * 1000.0));
        }
    }

    Report(name, &samples);
}

static void *MapRegion(size_t bytes) {
    void *region = mmap(NULL, bytes, PROT_READ | PROT_WRITE,
                        MAP_PRIVATE | MAP_ANON | MAP_NORESERVE, -1, 0);
    return region == MAP_FAILED ? NULL : region;
}

// ---- the submission-rate case ----

static void RunRateCase(id<MTLDevice> device) {
    id<MTLBuffer> small = [device newBufferWithLength:4096 options:MTLResourceStorageModeShared];
    id<MTLSharedEvent> event = [device newSharedEvent];
    double intervals[] = {1.0, 5.0, 20.0, 100.0};
    for (int index = 0; index < 4; index++) {
        char name[128];
        snprintf(name, sizeof(name), "plain blit every %.0f ms", intervals[index]);
        Measure(device, name, small, intervals[index], nil, NO);
    }

    for (int index = 0; index < 4; index++) {
        char name[128];
        snprintf(name, sizeof(name), "shared event + handler every %.0f ms", intervals[index]);
        Measure(device, name, small, intervals[index], event, YES);
    }
}

// ---- the guest-window case ----

static void RunWindowCase(id<MTLDevice> device) {
    const size_t gib = 1024ull * 1024 * 1024;
    const MTLResourceOptions untracked =
        MTLResourceStorageModeShared | MTLResourceHazardTrackingModeUntracked;
    void *lazy = MapRegion(4 * gib);
    id<MTLBuffer> window = [device newBufferWithBytesNoCopy:lazy length:4 * gib
                                                    options:untracked deallocator:nil];
    Measure(device, "4 GiB no-copy window, every 20 ms", window, 20.0, nil, NO);
    Measure(device, "4 GiB no-copy window, every 100 ms", window, 100.0, nil, NO);

    void *tracked = MapRegion(4 * gib);
    Measure(device, "4 GiB no-copy, hazard tracked, every 20 ms",
            [device newBufferWithBytesNoCopy:tracked length:4 * gib
                                     options:MTLResourceStorageModeShared deallocator:nil],
            20.0, nil, NO);

    // Five windows at once, as the host maps the guest's backing.
    for (int index = 0; index < 4; index++) {
        void *more = MapRegion(4 * gib);
        if (more) {
            (void)[device newBufferWithBytesNoCopy:more length:4 * gib
                                           options:untracked deallocator:nil];
        }
    }

    Measure(device, "five windows mapped, every 100 ms", window, 100.0, nil, NO);
}

// ---- the presenter case ----

static CAMetalLayer *gLayer;
static id<MTLCommandQueue> gPresentQueue;
static id<MTLTexture> gSharedTexture;
static id<MTLTexture> gPrivateTexture;
static volatile BOOL gPresentShared = YES;
static volatile BOOL gPresenting = YES;

static void PresenterLoop(void) {
    while (gPresenting) {
        @autoreleasepool {
            id<CAMetalDrawable> drawable = [gLayer nextDrawable];
            if (!drawable) {
                usleep(1000);
                continue;
            }

            id<MTLCommandBuffer> buffer = [gPresentQueue commandBuffer];
            id<MTLBlitCommandEncoder> blit = [buffer blitCommandEncoder];
            [blit copyFromTexture:(gPresentShared ? gSharedTexture : gPrivateTexture)
                      sourceSlice:0 sourceLevel:0 sourceOrigin:MTLOriginMake(0, 0, 0)
                       sourceSize:MTLSizeMake(drawable.texture.width, drawable.texture.height, 1)
                        toTexture:drawable.texture destinationSlice:0 destinationLevel:0
                destinationOrigin:MTLOriginMake(0, 0, 0)];
            [blit endEncoding];
            [buffer presentDrawable:drawable];
            [buffer commit];
        }
    }
}

static id<MTLTexture> MakeTexture(id<MTLDevice> device) {
    MTLTextureDescriptor *descriptor =
        [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatBGRA8Unorm
                                                           width:640 height:360 mipmapped:NO];
    descriptor.usage = MTLTextureUsageShaderRead | MTLTextureUsageRenderTarget;
    descriptor.storageMode = MTLStorageModePrivate;
    return [device newTextureWithDescriptor:descriptor];
}

// Writes the texture the presenter may be reading, as a guest frame does.
static void MeasureGuestFrames(id<MTLDevice> device, const char *name, double intervalMs) {
    id<MTLCommandQueue> queue = [device newCommandQueue];
    Samples samples = {.count = 0};
    for (int i = 0; i < SAMPLES + WARMUP; i++) {
        @autoreleasepool {
            id<MTLCommandBuffer> buffer = [queue commandBuffer];
            MTLRenderPassDescriptor *pass = [MTLRenderPassDescriptor renderPassDescriptor];
            pass.colorAttachments[0].texture = gSharedTexture;
            pass.colorAttachments[0].loadAction = MTLLoadActionClear;
            pass.colorAttachments[0].storeAction = MTLStoreActionStore;
            pass.colorAttachments[0].clearColor = MTLClearColorMake((i % 60) / 60.0, 0.2, 0.3, 1.0);
            [[buffer renderCommandEncoderWithDescriptor:pass] endEncoding];
            double committed = CACurrentMediaTime();
            [buffer commit];
            [buffer waitUntilCompleted];
            double finished = CACurrentMediaTime();
            if (i >= WARMUP) {
                samples.done[samples.count] = (finished - committed) * 1000.0;
                samples.kernel[samples.count] = (buffer.kernelStartTime - committed) * 1000.0;
                samples.count++;
            }

            double elapsed = (CACurrentMediaTime() - committed) * 1000.0;
            if (elapsed < intervalMs) usleep((useconds_t)((intervalMs - elapsed) * 1000.0));
        }
    }

    Report(name, &samples);
}

static void RunPresenterCase(id<MTLDevice> device) {
    [NSApplication sharedApplication];
    [NSApp setActivationPolicy:NSApplicationActivationPolicyAccessory];
    NSWindow *window = [[NSWindow alloc] initWithContentRect:NSMakeRect(0, 0, 640, 360)
                                                   styleMask:NSWindowStyleMaskBorderless
                                                     backing:NSBackingStoreBuffered
                                                       defer:NO];
    gLayer = [CAMetalLayer layer];
    gLayer.device = device;
    gLayer.pixelFormat = MTLPixelFormatBGRA8Unorm;
    gLayer.framebufferOnly = NO;
    gLayer.maximumDrawableCount = 3;
    gLayer.drawableSize = CGSizeMake(640, 360);
    window.contentView.layer = gLayer;
    window.contentView.wantsLayer = YES;
    [window orderFront:nil];

    gSharedTexture = MakeTexture(device);
    gPrivateTexture = MakeTexture(device);
    gPresentQueue = [device newCommandQueue];
    NSThread *presenter = [[NSThread alloc] initWithBlock:^{ PresenterLoop(); }];
    presenter.qualityOfService = NSQualityOfServiceUserInteractive;
    [presenter start];
    usleep(300000);

    gPresentShared = YES;
    MeasureGuestFrames(device, "presenter reads the written texture, 20 ms", 20.0);
    MeasureGuestFrames(device, "presenter reads the written texture, 100 ms", 100.0);
    gPresentShared = NO;
    usleep(200000);
    MeasureGuestFrames(device, "presenter reads its own texture, 20 ms", 20.0);
    MeasureGuestFrames(device, "presenter reads its own texture, 100 ms", 100.0);
    gPresenting = NO;
    usleep(200000);
}

// ---- the page-guard case ----

static uint8_t *gGuardWindow;
static size_t gGuardBytes;
static volatile int gGuardsPerRound = 0;
static volatile BOOL gGuarding = YES;

// Stands in for the host page guard: protect a page read-only, let it back in.
static void *GuardLoop(void *unused) {
    (void)unused;
    size_t cursor = 0;
    const size_t page = 16384;
    while (gGuarding) {
        int count = gGuardsPerRound;
        if (count <= 0) {
            usleep(2000);
            continue;
        }

        for (int i = 0; i < count; i++) {
            void *address = gGuardWindow + cursor;
            mprotect(address, page, PROT_READ);
            mprotect(address, page, PROT_READ | PROT_WRITE);
            ((volatile uint8_t *)address)[0] = 1;
            cursor += page * 3;
            if (cursor + page >= gGuardBytes) cursor = 0;
        }

        usleep(1000);
    }

    return NULL;
}

static void RunGuardCase(id<MTLDevice> device) {
    gGuardBytes = 4ull * 1024 * 1024 * 1024;
    gGuardWindow = MapRegion(gGuardBytes);
    id<MTLBuffer> window =
        [device newBufferWithBytesNoCopy:gGuardWindow length:gGuardBytes
                                 options:MTLResourceStorageModeShared |
                                         MTLResourceHazardTrackingModeUntracked
                             deallocator:nil];
    pthread_t guard;
    pthread_create(&guard, NULL, GuardLoop, NULL);

    int rounds[] = {0, 256, 2048};
    for (int index = 0; index < 3; index++) {
        char name[128];
        gGuardsPerRound = rounds[index];
        usleep(200000);
        snprintf(name, sizeof(name), "%d guarded pages per round, every 100 ms", rounds[index]);
        Measure(device, name, window, 100.0, nil, NO);
    }

    gGuardsPerRound = 0;
    gGuarding = NO;
    pthread_join(guard, NULL);
}

int main(int argc, const char *argv[]) {
    @autoreleasepool {
        id<MTLDevice> device = MTLCreateSystemDefaultDevice();
        if (!device) {
            printf("no Metal device\n");
            return 1;
        }

        const char *which = argc > 1 ? argv[1] : "all";
        printf("device=%s case=%s\n", device.name.UTF8String, which);
        BOOL all = strcmp(which, "all") == 0;
        if (all || strcmp(which, "rate") == 0) RunRateCase(device);
        if (all || strcmp(which, "windows") == 0) RunWindowCase(device);
        if (all || strcmp(which, "guard") == 0) RunGuardCase(device);
        if (all || strcmp(which, "presenter") == 0) RunPresenterCase(device);
    }

    return 0;
}
