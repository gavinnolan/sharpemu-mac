// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Gpu.Metal.Tests;

// The micro-kernels MetalMslPatternBenchTests runs. The helpers behind the //@DEV_PRELUDE and
// //@BUF_PRELUDE markers are what the translator emits for a Silent Hill mesh shader
// (Templates/resource_prelude.msl with this title's address-space constants filled in).
public sealed partial class MetalMslPatternBenchTests
{
    private const string DevPrelude = """
struct Gen5AddressRange
{
    ulong first_page;
    ulong page_count;
    device uint* base;
};

// The range this invocation resolved last. Guest accesses cluster: a loop-invariant
// base read every trip, a stream walking one buffer, a read-modify-write of the word it
// just read. A hit skips the binary search; the ranges cannot change while a dispatch
// runs, and the cache lives only as long as the invocation, so it is never stale. A
// zero page_count matches nothing, which is the empty cache.
struct Gen5RangeCache
{
    ulong first_page;
    ulong page_count;
    device uint* base;
};

// Finds the mapped range of a guest address; a miss records the page in the fault bitmap.
static inline device uint* sharpemu_resolve_device_address(device const Gen5AddressRange* ranges, uint range_count, device uint* faults, uint fault_words, thread Gen5RangeCache& cache, ulong address, thread bool& valid)
{
    ulong masked = address & 0xFFFFFFFFFFFFul;
    ulong page = masked >> 14u;
    if (page - cache.first_page < cache.page_count)
    {
        valid = true;
        ulong byte_offset = ((page - cache.first_page) << 14u) + (masked & 0x3FFFul);
        return (device uint*)((device uchar*)cache.base + byte_offset);
    }

    // range_count carries the table's entry count in its low 31 bits and, in bit 31, whether
    // the host appended a granule index after the entries. The index narrows the search to
    // the entries that can matter for this page; the result is the same upper bound the
    // full search finds, and a page outside the index's coverage takes the full search.
    uint entry_count = range_count & 0x7FFFFFFFu;
    uint low = 0u;
    uint high = entry_count;
    if ((range_count & 0x80000000u) != 0u)
    {
        device const uint* index = (device const uint*)(ranges + entry_count);
        ulong granule = (page >> index[0]) - (ulong(index[3]) << 32 | ulong(index[2]));
        if (granule < ulong(index[1]))
        {
            uint2 bucket = *(device const uint2*)(index + 4u + (uint)granule * 2u);
            low = bucket.x;
            high = bucket.y;
        }
    }
    for (uint step = 0u; step < 17u && low < high; step++)
    {
        uint middle = low + ((high - low) >> 1u);
        bool move_up = ranges[middle].first_page <= page;
        low = move_up ? middle + 1u : low;
        high = move_up ? high : middle;
    }
    valid = false;
    if (low != 0u)
    {
        Gen5AddressRange range = ranges[low - 1u];
        // The low 32 bits of page_count are the page count. Bit 63 marks a range in a guest
        // memory window this submission did not name resident, bits 32..61 its granule.
        // The access still goes through - the window's pages are mapped for the life of
        // the buffer - and the granule is reported in the feedback words (word 0 counts such
        // lookups, then from word 8 one bit per granule) so the host names it from then on.
        ulong page_count = range.page_count & 0xFFFFFFFFul;
        if (page - range.first_page < page_count)
        {
            ulong flags = range.page_count >> 62u;
            if (flags == 1ul)
            {
                // Bit 62: the range's backing chunks are single-copy released (their GPU alias
                // pages are a hole), bits 32..61 its first backing page. The hit is counted in
                // word 1 and its backing page set in the bitmap words 2 and 3 locate, so the
                // host gives the chunk back and keeps it. Word 4 set is the shadow mode, where
                // nothing was emptied and the access goes through; otherwise it reads zero and
                // a store is dropped. Never cached: every access to the range is seen.
                uint backing_page = ((uint)(range.page_count >> 32u) & 0x3FFFFFFFu) + (uint)(page - range.first_page);
                uint bitmap_word = backing_page >> 5u;
                if (bitmap_word < faults[3])
                {
                    atomic_fetch_or_explicit((device atomic_uint*)(faults + faults[2] + bitmap_word), 1u << (backing_page & 31u), memory_order_relaxed);
                }
                atomic_fetch_add_explicit((device atomic_uint*)(faults + 1u), 1u, memory_order_relaxed);
                if (faults[4] == 0u)
                {
                    return nullptr;
                }
                valid = true;
                ulong trap_offset = ((page - range.first_page) << 14u) + (masked & 0x3FFFul);
                return (device uint*)((device uchar*)range.base + trap_offset);
            }
            if (flags != 0ul)
            {
                uint granule = (uint)(range.page_count >> 32u) & 0x3FFFFFFFu;
                atomic_fetch_or_explicit((device atomic_uint*)(faults + 8u + (granule >> 5u)), 1u << (granule & 31u), memory_order_relaxed);
                atomic_fetch_add_explicit((device atomic_uint*)faults, 1u, memory_order_relaxed);
            }
            valid = true;
            cache.first_page = range.first_page;
            cache.page_count = page_count;
            cache.base = range.base;
            ulong byte_offset = ((page - range.first_page) << 14u) + (masked & 0x3FFFul);
            return (device uint*)((device uchar*)range.base + byte_offset);
        }
    }
    uint page_index = (uint)page;
    uint word = page_index >> 5u;
    if (word < fault_words)
    {
        atomic_fetch_or_explicit((device atomic_uint*)(faults + word), 1u << (page_index & 31u), memory_order_relaxed);
    }
    return nullptr;
}

static inline uint sharpemu_load_device_dword(device const Gen5AddressRange* ranges, uint range_count, device uint* faults, uint fault_words, thread Gen5RangeCache& cache, ulong address)
{
    bool valid = false;
    device uint* word = sharpemu_resolve_device_address(ranges, range_count, faults, fault_words, cache, address, valid);
    return valid ? *word : 0u;
}

// Resolves the first dword of a contiguous multi-dword read once for the whole span.
// A span that stays inside one 16 KB page is contiguous host memory, so its dwords
// index off the one pointer; one that crosses a page reports invalid and the caller
// reads it dword by dword, which is what every dword would have cost before.
static inline device uint* sharpemu_resolve_device_span(device const Gen5AddressRange* ranges, uint range_count, device uint* faults, uint fault_words, thread Gen5RangeCache& cache, ulong address, uint bytes, thread bool& valid)
{
    device uint* first = sharpemu_resolve_device_address(ranges, range_count, faults, fault_words, cache, address, valid);
    valid = valid && (address & 0x3FFFul) + bytes <= 0x4000ul;
    return first;
}

// An unaligned value assembled from the two aligned dwords it spans.
static inline uint sharpemu_load_device_unaligned(device const Gen5AddressRange* ranges, uint range_count, device uint* faults, uint fault_words, thread Gen5RangeCache& cache, ulong address, uint count)
{
    uint alignment = (uint)(address & 3ul);
    ulong aligned = address & ~3ul;
    uint shift = alignment * 8u;
    uint low = sharpemu_load_device_dword(ranges, range_count, faults, fault_words, cache, aligned);
    uint high = (alignment + count > 4u) ? sharpemu_load_device_dword(ranges, range_count, faults, fault_words, cache, aligned + 4ul) : 0u;
    return (low >> shift) | (shift == 0u ? 0u : (high << (32u - shift)));
}

""";

    private const string BufPrelude = """
static inline uint sharpemu_load_dword(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    return index < (bytes >> 2) ? b[index] : 0u;
}

// Up to four consecutive raw dwords of an S_BUFFER_LOAD/BUFFER_LOAD_DWORDXn, each what
// sharpemu_load_dword reads at addr + 4 * i. A run that lies inside the binding is one
// bounds check and adjacent loads; one that crosses the end reads dword by dword.
template <int N>
static inline uint4 sharpemu_load_dwords(device uint* b, uint bytes, uint addr)
{
    uint4 result = uint4(0u);
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && (uint)N <= words - index)
    {
        for (int i = 0; i < N; i++)
        {
            result[i] = b[index + (uint)i];
        }
    }
    else
    {
        for (int i = 0; i < N; i++)
        {
            result[i] = sharpemu_load_dword(b, bytes, addr + (uint)(4 * i));
        }
    }
    return result;
}

""";

    private static readonly Dictionary<string, string> RawKernels = new()
    {
        ["buf1_perlane_constant_checked"] = """
// bench group=buf_one_perlane_constant variant=checked threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE_CONSTANT

static inline uint load1_select(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint v = b[index > last ? last : index];
    return index < words ? v : 0u;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    constant uint* b = cdata;
    uint bytes = prm.y;
    uint acc = tid;
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (0u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (16384u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (32768u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (49152u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (65536u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (81920u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (98304u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (114688u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (131072u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (147456u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (163840u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (180224u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (196608u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (212992u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (229376u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (245760u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (262144u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (278528u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (294912u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (311296u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (327680u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (344064u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (360448u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (376832u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (393216u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (409600u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (425984u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (442368u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (458752u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (475136u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (491520u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (507904u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (524288u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (540672u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (557056u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (573440u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (589824u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (606208u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (622592u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (638976u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (655360u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (671744u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (688128u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (704512u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (720896u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (737280u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (753664u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (770048u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (786432u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (802816u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (819200u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (835584u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (851968u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (868352u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (884736u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (901120u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (917504u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (933888u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (950272u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (966656u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (983040u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (999424u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (1015808u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (1032192u + ((tid * 4u) & 0x3FFFu)));
    out[tid] = acc;
}
""",
        ["buf1_perlane_constant_raw"] = """
// bench group=buf_one_perlane_constant variant=raw threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE_CONSTANT

static inline uint load1_select(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint v = b[index > last ? last : index];
    return index < words ? v : 0u;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    constant uint* b = cdata;
    uint bytes = prm.y;
    uint acc = tid;
    acc = acc * 3u + b[(0u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(16384u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(32768u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(49152u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(65536u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(81920u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(98304u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(114688u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(131072u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(147456u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(163840u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(180224u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(196608u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(212992u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(229376u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(245760u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(262144u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(278528u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(294912u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(311296u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(327680u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(344064u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(360448u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(376832u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(393216u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(409600u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(425984u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(442368u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(458752u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(475136u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(491520u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(507904u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(524288u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(540672u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(557056u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(573440u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(589824u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(606208u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(622592u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(638976u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(655360u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(671744u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(688128u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(704512u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(720896u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(737280u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(753664u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(770048u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(786432u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(802816u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(819200u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(835584u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(851968u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(868352u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(884736u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(901120u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(917504u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(933888u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(950272u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(966656u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(983040u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(999424u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(1015808u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(1032192u + ((tid * 4u) & 0x3FFFu)) >> 2];
    out[tid] = acc;
}
""",
        ["buf1_perlane_constant_select"] = """
// bench group=buf_one_perlane_constant variant=select threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE_CONSTANT

static inline uint load1_select(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint v = b[index > last ? last : index];
    return index < words ? v : 0u;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    constant uint* b = cdata;
    uint bytes = prm.y;
    uint acc = tid;
    acc = acc * 3u + load1_select(b, bytes, (0u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (16384u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (32768u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (49152u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (65536u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (81920u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (98304u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (114688u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (131072u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (147456u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (163840u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (180224u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (196608u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (212992u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (229376u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (245760u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (262144u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (278528u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (294912u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (311296u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (327680u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (344064u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (360448u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (376832u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (393216u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (409600u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (425984u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (442368u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (458752u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (475136u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (491520u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (507904u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (524288u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (540672u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (557056u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (573440u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (589824u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (606208u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (622592u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (638976u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (655360u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (671744u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (688128u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (704512u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (720896u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (737280u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (753664u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (770048u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (786432u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (802816u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (819200u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (835584u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (851968u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (868352u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (884736u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (901120u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (917504u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (933888u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (950272u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (966656u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (983040u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (999424u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (1015808u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (1032192u + ((tid * 4u) & 0x3FFFu)));
    out[tid] = acc;
}
""",
        ["buf1_perlane_device_checked"] = """
// bench group=buf_one_perlane_device variant=checked threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE

static inline uint load1_select(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint v = b[index > last ? last : index];
    return index < words ? v : 0u;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    device uint* b = data;
    uint bytes = prm.y;
    uint acc = tid;
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (0u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (16384u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (32768u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (49152u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (65536u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (81920u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (98304u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (114688u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (131072u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (147456u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (163840u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (180224u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (196608u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (212992u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (229376u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (245760u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (262144u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (278528u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (294912u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (311296u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (327680u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (344064u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (360448u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (376832u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (393216u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (409600u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (425984u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (442368u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (458752u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (475136u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (491520u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (507904u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (524288u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (540672u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (557056u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (573440u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (589824u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (606208u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (622592u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (638976u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (655360u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (671744u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (688128u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (704512u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (720896u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (737280u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (753664u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (770048u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (786432u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (802816u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (819200u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (835584u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (851968u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (868352u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (884736u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (901120u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (917504u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (933888u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (950272u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (966656u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (983040u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (999424u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (1015808u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + sharpemu_load_dword(b, bytes, (1032192u + ((tid * 4u) & 0x3FFFu)));
    out[tid] = acc;
}
""",
        ["buf1_perlane_device_raw"] = """
// bench group=buf_one_perlane_device variant=raw threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE

static inline uint load1_select(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint v = b[index > last ? last : index];
    return index < words ? v : 0u;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    device uint* b = data;
    uint bytes = prm.y;
    uint acc = tid;
    acc = acc * 3u + b[(0u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(16384u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(32768u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(49152u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(65536u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(81920u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(98304u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(114688u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(131072u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(147456u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(163840u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(180224u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(196608u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(212992u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(229376u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(245760u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(262144u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(278528u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(294912u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(311296u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(327680u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(344064u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(360448u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(376832u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(393216u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(409600u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(425984u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(442368u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(458752u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(475136u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(491520u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(507904u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(524288u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(540672u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(557056u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(573440u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(589824u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(606208u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(622592u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(638976u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(655360u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(671744u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(688128u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(704512u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(720896u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(737280u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(753664u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(770048u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(786432u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(802816u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(819200u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(835584u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(851968u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(868352u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(884736u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(901120u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(917504u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(933888u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(950272u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(966656u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(983040u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(999424u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(1015808u + ((tid * 4u) & 0x3FFFu)) >> 2];
    acc = acc * 3u + b[(1032192u + ((tid * 4u) & 0x3FFFu)) >> 2];
    out[tid] = acc;
}
""",
        ["buf1_perlane_device_select"] = """
// bench group=buf_one_perlane_device variant=select threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE

static inline uint load1_select(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint v = b[index > last ? last : index];
    return index < words ? v : 0u;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    device uint* b = data;
    uint bytes = prm.y;
    uint acc = tid;
    acc = acc * 3u + load1_select(b, bytes, (0u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (16384u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (32768u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (49152u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (65536u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (81920u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (98304u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (114688u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (131072u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (147456u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (163840u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (180224u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (196608u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (212992u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (229376u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (245760u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (262144u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (278528u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (294912u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (311296u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (327680u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (344064u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (360448u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (376832u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (393216u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (409600u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (425984u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (442368u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (458752u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (475136u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (491520u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (507904u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (524288u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (540672u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (557056u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (573440u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (589824u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (606208u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (622592u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (638976u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (655360u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (671744u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (688128u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (704512u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (720896u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (737280u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (753664u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (770048u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (786432u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (802816u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (819200u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (835584u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (851968u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (868352u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (884736u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (901120u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (917504u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (933888u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (950272u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (966656u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (983040u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (999424u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (1015808u + ((tid * 4u) & 0x3FFFu)));
    acc = acc * 3u + load1_select(b, bytes, (1032192u + ((tid * 4u) & 0x3FFFu)));
    out[tid] = acc;
}
""",
        ["buf1_uniform_constant_checked"] = """
// bench group=buf_one_uniform_constant variant=checked threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE_CONSTANT

static inline uint load1_select(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint v = b[index > last ? last : index];
    return index < words ? v : 0u;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    constant uint* b = cdata;
    uint bytes = prm.y;
    uint acc = tid;
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 0u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 4u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 8u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 12u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 16u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 20u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 24u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 28u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 32u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 36u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 40u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 44u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 48u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 52u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 56u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 60u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 64u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 68u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 72u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 76u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 80u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 84u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 88u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 92u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 96u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 100u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 104u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 108u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 112u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 116u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 120u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 124u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 128u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 132u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 136u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 140u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 144u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 148u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 152u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 156u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 160u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 164u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 168u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 172u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 176u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 180u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 184u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 188u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 192u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 196u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 200u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 204u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 208u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 212u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 216u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 220u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 224u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 228u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 232u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 236u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 240u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 244u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 248u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 252u);
    out[tid] = acc;
}
""",
        ["buf1_uniform_constant_raw"] = """
// bench group=buf_one_uniform_constant variant=raw threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE_CONSTANT

static inline uint load1_select(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint v = b[index > last ? last : index];
    return index < words ? v : 0u;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    constant uint* b = cdata;
    uint bytes = prm.y;
    uint acc = tid;
    acc = acc * 3u + b[0u >> 2];
    acc = acc * 3u + b[4u >> 2];
    acc = acc * 3u + b[8u >> 2];
    acc = acc * 3u + b[12u >> 2];
    acc = acc * 3u + b[16u >> 2];
    acc = acc * 3u + b[20u >> 2];
    acc = acc * 3u + b[24u >> 2];
    acc = acc * 3u + b[28u >> 2];
    acc = acc * 3u + b[32u >> 2];
    acc = acc * 3u + b[36u >> 2];
    acc = acc * 3u + b[40u >> 2];
    acc = acc * 3u + b[44u >> 2];
    acc = acc * 3u + b[48u >> 2];
    acc = acc * 3u + b[52u >> 2];
    acc = acc * 3u + b[56u >> 2];
    acc = acc * 3u + b[60u >> 2];
    acc = acc * 3u + b[64u >> 2];
    acc = acc * 3u + b[68u >> 2];
    acc = acc * 3u + b[72u >> 2];
    acc = acc * 3u + b[76u >> 2];
    acc = acc * 3u + b[80u >> 2];
    acc = acc * 3u + b[84u >> 2];
    acc = acc * 3u + b[88u >> 2];
    acc = acc * 3u + b[92u >> 2];
    acc = acc * 3u + b[96u >> 2];
    acc = acc * 3u + b[100u >> 2];
    acc = acc * 3u + b[104u >> 2];
    acc = acc * 3u + b[108u >> 2];
    acc = acc * 3u + b[112u >> 2];
    acc = acc * 3u + b[116u >> 2];
    acc = acc * 3u + b[120u >> 2];
    acc = acc * 3u + b[124u >> 2];
    acc = acc * 3u + b[128u >> 2];
    acc = acc * 3u + b[132u >> 2];
    acc = acc * 3u + b[136u >> 2];
    acc = acc * 3u + b[140u >> 2];
    acc = acc * 3u + b[144u >> 2];
    acc = acc * 3u + b[148u >> 2];
    acc = acc * 3u + b[152u >> 2];
    acc = acc * 3u + b[156u >> 2];
    acc = acc * 3u + b[160u >> 2];
    acc = acc * 3u + b[164u >> 2];
    acc = acc * 3u + b[168u >> 2];
    acc = acc * 3u + b[172u >> 2];
    acc = acc * 3u + b[176u >> 2];
    acc = acc * 3u + b[180u >> 2];
    acc = acc * 3u + b[184u >> 2];
    acc = acc * 3u + b[188u >> 2];
    acc = acc * 3u + b[192u >> 2];
    acc = acc * 3u + b[196u >> 2];
    acc = acc * 3u + b[200u >> 2];
    acc = acc * 3u + b[204u >> 2];
    acc = acc * 3u + b[208u >> 2];
    acc = acc * 3u + b[212u >> 2];
    acc = acc * 3u + b[216u >> 2];
    acc = acc * 3u + b[220u >> 2];
    acc = acc * 3u + b[224u >> 2];
    acc = acc * 3u + b[228u >> 2];
    acc = acc * 3u + b[232u >> 2];
    acc = acc * 3u + b[236u >> 2];
    acc = acc * 3u + b[240u >> 2];
    acc = acc * 3u + b[244u >> 2];
    acc = acc * 3u + b[248u >> 2];
    acc = acc * 3u + b[252u >> 2];
    out[tid] = acc;
}
""",
        ["buf1_uniform_constant_select"] = """
// bench group=buf_one_uniform_constant variant=select threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE_CONSTANT

static inline uint load1_select(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint v = b[index > last ? last : index];
    return index < words ? v : 0u;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    constant uint* b = cdata;
    uint bytes = prm.y;
    uint acc = tid;
    acc = acc * 3u + load1_select(b, bytes, 0u);
    acc = acc * 3u + load1_select(b, bytes, 4u);
    acc = acc * 3u + load1_select(b, bytes, 8u);
    acc = acc * 3u + load1_select(b, bytes, 12u);
    acc = acc * 3u + load1_select(b, bytes, 16u);
    acc = acc * 3u + load1_select(b, bytes, 20u);
    acc = acc * 3u + load1_select(b, bytes, 24u);
    acc = acc * 3u + load1_select(b, bytes, 28u);
    acc = acc * 3u + load1_select(b, bytes, 32u);
    acc = acc * 3u + load1_select(b, bytes, 36u);
    acc = acc * 3u + load1_select(b, bytes, 40u);
    acc = acc * 3u + load1_select(b, bytes, 44u);
    acc = acc * 3u + load1_select(b, bytes, 48u);
    acc = acc * 3u + load1_select(b, bytes, 52u);
    acc = acc * 3u + load1_select(b, bytes, 56u);
    acc = acc * 3u + load1_select(b, bytes, 60u);
    acc = acc * 3u + load1_select(b, bytes, 64u);
    acc = acc * 3u + load1_select(b, bytes, 68u);
    acc = acc * 3u + load1_select(b, bytes, 72u);
    acc = acc * 3u + load1_select(b, bytes, 76u);
    acc = acc * 3u + load1_select(b, bytes, 80u);
    acc = acc * 3u + load1_select(b, bytes, 84u);
    acc = acc * 3u + load1_select(b, bytes, 88u);
    acc = acc * 3u + load1_select(b, bytes, 92u);
    acc = acc * 3u + load1_select(b, bytes, 96u);
    acc = acc * 3u + load1_select(b, bytes, 100u);
    acc = acc * 3u + load1_select(b, bytes, 104u);
    acc = acc * 3u + load1_select(b, bytes, 108u);
    acc = acc * 3u + load1_select(b, bytes, 112u);
    acc = acc * 3u + load1_select(b, bytes, 116u);
    acc = acc * 3u + load1_select(b, bytes, 120u);
    acc = acc * 3u + load1_select(b, bytes, 124u);
    acc = acc * 3u + load1_select(b, bytes, 128u);
    acc = acc * 3u + load1_select(b, bytes, 132u);
    acc = acc * 3u + load1_select(b, bytes, 136u);
    acc = acc * 3u + load1_select(b, bytes, 140u);
    acc = acc * 3u + load1_select(b, bytes, 144u);
    acc = acc * 3u + load1_select(b, bytes, 148u);
    acc = acc * 3u + load1_select(b, bytes, 152u);
    acc = acc * 3u + load1_select(b, bytes, 156u);
    acc = acc * 3u + load1_select(b, bytes, 160u);
    acc = acc * 3u + load1_select(b, bytes, 164u);
    acc = acc * 3u + load1_select(b, bytes, 168u);
    acc = acc * 3u + load1_select(b, bytes, 172u);
    acc = acc * 3u + load1_select(b, bytes, 176u);
    acc = acc * 3u + load1_select(b, bytes, 180u);
    acc = acc * 3u + load1_select(b, bytes, 184u);
    acc = acc * 3u + load1_select(b, bytes, 188u);
    acc = acc * 3u + load1_select(b, bytes, 192u);
    acc = acc * 3u + load1_select(b, bytes, 196u);
    acc = acc * 3u + load1_select(b, bytes, 200u);
    acc = acc * 3u + load1_select(b, bytes, 204u);
    acc = acc * 3u + load1_select(b, bytes, 208u);
    acc = acc * 3u + load1_select(b, bytes, 212u);
    acc = acc * 3u + load1_select(b, bytes, 216u);
    acc = acc * 3u + load1_select(b, bytes, 220u);
    acc = acc * 3u + load1_select(b, bytes, 224u);
    acc = acc * 3u + load1_select(b, bytes, 228u);
    acc = acc * 3u + load1_select(b, bytes, 232u);
    acc = acc * 3u + load1_select(b, bytes, 236u);
    acc = acc * 3u + load1_select(b, bytes, 240u);
    acc = acc * 3u + load1_select(b, bytes, 244u);
    acc = acc * 3u + load1_select(b, bytes, 248u);
    acc = acc * 3u + load1_select(b, bytes, 252u);
    out[tid] = acc;
}
""",
        ["buf1_uniform_device_checked"] = """
// bench group=buf_one_uniform_device variant=checked threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE

static inline uint load1_select(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint v = b[index > last ? last : index];
    return index < words ? v : 0u;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    device uint* b = data;
    uint bytes = prm.y;
    uint acc = tid;
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 0u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 4u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 8u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 12u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 16u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 20u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 24u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 28u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 32u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 36u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 40u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 44u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 48u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 52u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 56u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 60u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 64u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 68u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 72u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 76u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 80u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 84u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 88u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 92u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 96u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 100u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 104u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 108u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 112u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 116u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 120u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 124u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 128u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 132u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 136u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 140u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 144u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 148u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 152u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 156u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 160u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 164u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 168u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 172u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 176u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 180u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 184u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 188u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 192u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 196u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 200u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 204u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 208u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 212u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 216u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 220u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 224u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 228u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 232u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 236u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 240u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 244u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 248u);
    acc = acc * 3u + sharpemu_load_dword(b, bytes, 252u);
    out[tid] = acc;
}
""",
        ["buf1_uniform_device_raw"] = """
// bench group=buf_one_uniform_device variant=raw threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE

static inline uint load1_select(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint v = b[index > last ? last : index];
    return index < words ? v : 0u;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    device uint* b = data;
    uint bytes = prm.y;
    uint acc = tid;
    acc = acc * 3u + b[0u >> 2];
    acc = acc * 3u + b[4u >> 2];
    acc = acc * 3u + b[8u >> 2];
    acc = acc * 3u + b[12u >> 2];
    acc = acc * 3u + b[16u >> 2];
    acc = acc * 3u + b[20u >> 2];
    acc = acc * 3u + b[24u >> 2];
    acc = acc * 3u + b[28u >> 2];
    acc = acc * 3u + b[32u >> 2];
    acc = acc * 3u + b[36u >> 2];
    acc = acc * 3u + b[40u >> 2];
    acc = acc * 3u + b[44u >> 2];
    acc = acc * 3u + b[48u >> 2];
    acc = acc * 3u + b[52u >> 2];
    acc = acc * 3u + b[56u >> 2];
    acc = acc * 3u + b[60u >> 2];
    acc = acc * 3u + b[64u >> 2];
    acc = acc * 3u + b[68u >> 2];
    acc = acc * 3u + b[72u >> 2];
    acc = acc * 3u + b[76u >> 2];
    acc = acc * 3u + b[80u >> 2];
    acc = acc * 3u + b[84u >> 2];
    acc = acc * 3u + b[88u >> 2];
    acc = acc * 3u + b[92u >> 2];
    acc = acc * 3u + b[96u >> 2];
    acc = acc * 3u + b[100u >> 2];
    acc = acc * 3u + b[104u >> 2];
    acc = acc * 3u + b[108u >> 2];
    acc = acc * 3u + b[112u >> 2];
    acc = acc * 3u + b[116u >> 2];
    acc = acc * 3u + b[120u >> 2];
    acc = acc * 3u + b[124u >> 2];
    acc = acc * 3u + b[128u >> 2];
    acc = acc * 3u + b[132u >> 2];
    acc = acc * 3u + b[136u >> 2];
    acc = acc * 3u + b[140u >> 2];
    acc = acc * 3u + b[144u >> 2];
    acc = acc * 3u + b[148u >> 2];
    acc = acc * 3u + b[152u >> 2];
    acc = acc * 3u + b[156u >> 2];
    acc = acc * 3u + b[160u >> 2];
    acc = acc * 3u + b[164u >> 2];
    acc = acc * 3u + b[168u >> 2];
    acc = acc * 3u + b[172u >> 2];
    acc = acc * 3u + b[176u >> 2];
    acc = acc * 3u + b[180u >> 2];
    acc = acc * 3u + b[184u >> 2];
    acc = acc * 3u + b[188u >> 2];
    acc = acc * 3u + b[192u >> 2];
    acc = acc * 3u + b[196u >> 2];
    acc = acc * 3u + b[200u >> 2];
    acc = acc * 3u + b[204u >> 2];
    acc = acc * 3u + b[208u >> 2];
    acc = acc * 3u + b[212u >> 2];
    acc = acc * 3u + b[216u >> 2];
    acc = acc * 3u + b[220u >> 2];
    acc = acc * 3u + b[224u >> 2];
    acc = acc * 3u + b[228u >> 2];
    acc = acc * 3u + b[232u >> 2];
    acc = acc * 3u + b[236u >> 2];
    acc = acc * 3u + b[240u >> 2];
    acc = acc * 3u + b[244u >> 2];
    acc = acc * 3u + b[248u >> 2];
    acc = acc * 3u + b[252u >> 2];
    out[tid] = acc;
}
""",
        ["buf1_uniform_device_select"] = """
// bench group=buf_one_uniform_device variant=select threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE

static inline uint load1_select(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint v = b[index > last ? last : index];
    return index < words ? v : 0u;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    device uint* b = data;
    uint bytes = prm.y;
    uint acc = tid;
    acc = acc * 3u + load1_select(b, bytes, 0u);
    acc = acc * 3u + load1_select(b, bytes, 4u);
    acc = acc * 3u + load1_select(b, bytes, 8u);
    acc = acc * 3u + load1_select(b, bytes, 12u);
    acc = acc * 3u + load1_select(b, bytes, 16u);
    acc = acc * 3u + load1_select(b, bytes, 20u);
    acc = acc * 3u + load1_select(b, bytes, 24u);
    acc = acc * 3u + load1_select(b, bytes, 28u);
    acc = acc * 3u + load1_select(b, bytes, 32u);
    acc = acc * 3u + load1_select(b, bytes, 36u);
    acc = acc * 3u + load1_select(b, bytes, 40u);
    acc = acc * 3u + load1_select(b, bytes, 44u);
    acc = acc * 3u + load1_select(b, bytes, 48u);
    acc = acc * 3u + load1_select(b, bytes, 52u);
    acc = acc * 3u + load1_select(b, bytes, 56u);
    acc = acc * 3u + load1_select(b, bytes, 60u);
    acc = acc * 3u + load1_select(b, bytes, 64u);
    acc = acc * 3u + load1_select(b, bytes, 68u);
    acc = acc * 3u + load1_select(b, bytes, 72u);
    acc = acc * 3u + load1_select(b, bytes, 76u);
    acc = acc * 3u + load1_select(b, bytes, 80u);
    acc = acc * 3u + load1_select(b, bytes, 84u);
    acc = acc * 3u + load1_select(b, bytes, 88u);
    acc = acc * 3u + load1_select(b, bytes, 92u);
    acc = acc * 3u + load1_select(b, bytes, 96u);
    acc = acc * 3u + load1_select(b, bytes, 100u);
    acc = acc * 3u + load1_select(b, bytes, 104u);
    acc = acc * 3u + load1_select(b, bytes, 108u);
    acc = acc * 3u + load1_select(b, bytes, 112u);
    acc = acc * 3u + load1_select(b, bytes, 116u);
    acc = acc * 3u + load1_select(b, bytes, 120u);
    acc = acc * 3u + load1_select(b, bytes, 124u);
    acc = acc * 3u + load1_select(b, bytes, 128u);
    acc = acc * 3u + load1_select(b, bytes, 132u);
    acc = acc * 3u + load1_select(b, bytes, 136u);
    acc = acc * 3u + load1_select(b, bytes, 140u);
    acc = acc * 3u + load1_select(b, bytes, 144u);
    acc = acc * 3u + load1_select(b, bytes, 148u);
    acc = acc * 3u + load1_select(b, bytes, 152u);
    acc = acc * 3u + load1_select(b, bytes, 156u);
    acc = acc * 3u + load1_select(b, bytes, 160u);
    acc = acc * 3u + load1_select(b, bytes, 164u);
    acc = acc * 3u + load1_select(b, bytes, 168u);
    acc = acc * 3u + load1_select(b, bytes, 172u);
    acc = acc * 3u + load1_select(b, bytes, 176u);
    acc = acc * 3u + load1_select(b, bytes, 180u);
    acc = acc * 3u + load1_select(b, bytes, 184u);
    acc = acc * 3u + load1_select(b, bytes, 188u);
    acc = acc * 3u + load1_select(b, bytes, 192u);
    acc = acc * 3u + load1_select(b, bytes, 196u);
    acc = acc * 3u + load1_select(b, bytes, 200u);
    acc = acc * 3u + load1_select(b, bytes, 204u);
    acc = acc * 3u + load1_select(b, bytes, 208u);
    acc = acc * 3u + load1_select(b, bytes, 212u);
    acc = acc * 3u + load1_select(b, bytes, 216u);
    acc = acc * 3u + load1_select(b, bytes, 220u);
    acc = acc * 3u + load1_select(b, bytes, 224u);
    acc = acc * 3u + load1_select(b, bytes, 228u);
    acc = acc * 3u + load1_select(b, bytes, 232u);
    acc = acc * 3u + load1_select(b, bytes, 236u);
    acc = acc * 3u + load1_select(b, bytes, 240u);
    acc = acc * 3u + load1_select(b, bytes, 244u);
    acc = acc * 3u + load1_select(b, bytes, 248u);
    acc = acc * 3u + load1_select(b, bytes, 252u);
    out[tid] = acc;
}
""",
        ["buf2_perlane_constant_checked"] = """
// bench group=buf_two_perlane_constant variant=checked threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE_CONSTANT

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint2 slow2(constant uint* b, uint bytes, uint addr)
{
    return uint2(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u));
}
static inline uint2 load2_outlined(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 2u <= words - index)
    {
        return uint2(b[index], b[index + 1u]);
    }
    return slow2(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint2 load2_select(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint2 r = uint2(0u);
    for (int i = 0; i < 2; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    constant uint* b = cdata;
    uint bytes = prm.y;
    uint acc = tid;
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (0u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (65536u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (131072u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (196608u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (262144u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (327680u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (393216u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (458752u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (524288u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (589824u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (655360u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (720896u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (786432u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (851968u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (917504u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (983040u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1048576u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1114112u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1179648u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1245184u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1310720u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1376256u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1441792u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1507328u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1572864u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1638400u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1703936u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1769472u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1835008u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1900544u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1966080u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (2031616u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    out[tid] = acc;
}
""",
        ["buf2_perlane_constant_outlined"] = """
// bench group=buf_two_perlane_constant variant=outlined threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE_CONSTANT

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint2 slow2(constant uint* b, uint bytes, uint addr)
{
    return uint2(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u));
}
static inline uint2 load2_outlined(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 2u <= words - index)
    {
        return uint2(b[index], b[index + 1u]);
    }
    return slow2(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint2 load2_select(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint2 r = uint2(0u);
    for (int i = 0; i < 2; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    constant uint* b = cdata;
    uint bytes = prm.y;
    uint acc = tid;
    { uint2 w = load2_outlined(b, bytes, (0u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (65536u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (131072u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (196608u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (262144u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (327680u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (393216u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (458752u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (524288u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (589824u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (655360u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (720896u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (786432u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (851968u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (917504u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (983040u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1048576u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1114112u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1179648u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1245184u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1310720u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1376256u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1441792u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1507328u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1572864u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1638400u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1703936u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1769472u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1835008u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1900544u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1966080u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (2031616u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    out[tid] = acc;
}
""",
        ["buf2_perlane_constant_raw"] = """
// bench group=buf_two_perlane_constant variant=raw threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE_CONSTANT

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint2 slow2(constant uint* b, uint bytes, uint addr)
{
    return uint2(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u));
}
static inline uint2 load2_outlined(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 2u <= words - index)
    {
        return uint2(b[index], b[index + 1u]);
    }
    return slow2(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint2 load2_select(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint2 r = uint2(0u);
    for (int i = 0; i < 2; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    constant uint* b = cdata;
    uint bytes = prm.y;
    uint acc = tid;
    { uint i = (0u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (65536u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (131072u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (196608u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (262144u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (327680u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (393216u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (458752u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (524288u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (589824u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (655360u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (720896u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (786432u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (851968u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (917504u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (983040u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1048576u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1114112u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1179648u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1245184u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1310720u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1376256u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1441792u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1507328u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1572864u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1638400u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1703936u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1769472u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1835008u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1900544u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1966080u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (2031616u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    out[tid] = acc;
}
""",
        ["buf2_perlane_constant_select"] = """
// bench group=buf_two_perlane_constant variant=select threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE_CONSTANT

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint2 slow2(constant uint* b, uint bytes, uint addr)
{
    return uint2(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u));
}
static inline uint2 load2_outlined(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 2u <= words - index)
    {
        return uint2(b[index], b[index + 1u]);
    }
    return slow2(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint2 load2_select(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint2 r = uint2(0u);
    for (int i = 0; i < 2; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    constant uint* b = cdata;
    uint bytes = prm.y;
    uint acc = tid;
    { uint2 w = load2_select(b, bytes, (0u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (65536u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (131072u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (196608u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (262144u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (327680u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (393216u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (458752u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (524288u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (589824u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (655360u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (720896u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (786432u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (851968u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (917504u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (983040u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1048576u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1114112u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1179648u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1245184u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1310720u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1376256u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1441792u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1507328u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1572864u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1638400u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1703936u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1769472u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1835008u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1900544u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1966080u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (2031616u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    out[tid] = acc;
}
""",
        ["buf2_perlane_device_checked"] = """
// bench group=buf_two_perlane_device variant=checked threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint2 slow2(device uint* b, uint bytes, uint addr)
{
    return uint2(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u));
}
static inline uint2 load2_outlined(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 2u <= words - index)
    {
        return uint2(b[index], b[index + 1u]);
    }
    return slow2(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint2 load2_select(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint2 r = uint2(0u);
    for (int i = 0; i < 2; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    device uint* b = data;
    uint bytes = prm.y;
    uint acc = tid;
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (0u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (65536u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (131072u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (196608u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (262144u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (327680u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (393216u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (458752u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (524288u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (589824u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (655360u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (720896u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (786432u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (851968u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (917504u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (983040u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1048576u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1114112u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1179648u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1245184u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1310720u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1376256u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1441792u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1507328u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1572864u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1638400u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1703936u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1769472u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1835008u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1900544u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (1966080u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, (2031616u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    out[tid] = acc;
}
""",
        ["buf2_perlane_device_outlined"] = """
// bench group=buf_two_perlane_device variant=outlined threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint2 slow2(device uint* b, uint bytes, uint addr)
{
    return uint2(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u));
}
static inline uint2 load2_outlined(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 2u <= words - index)
    {
        return uint2(b[index], b[index + 1u]);
    }
    return slow2(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint2 load2_select(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint2 r = uint2(0u);
    for (int i = 0; i < 2; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    device uint* b = data;
    uint bytes = prm.y;
    uint acc = tid;
    { uint2 w = load2_outlined(b, bytes, (0u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (65536u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (131072u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (196608u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (262144u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (327680u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (393216u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (458752u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (524288u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (589824u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (655360u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (720896u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (786432u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (851968u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (917504u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (983040u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1048576u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1114112u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1179648u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1245184u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1310720u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1376256u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1441792u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1507328u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1572864u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1638400u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1703936u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1769472u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1835008u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1900544u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (1966080u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, (2031616u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    out[tid] = acc;
}
""",
        ["buf2_perlane_device_raw"] = """
// bench group=buf_two_perlane_device variant=raw threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint2 slow2(device uint* b, uint bytes, uint addr)
{
    return uint2(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u));
}
static inline uint2 load2_outlined(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 2u <= words - index)
    {
        return uint2(b[index], b[index + 1u]);
    }
    return slow2(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint2 load2_select(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint2 r = uint2(0u);
    for (int i = 0; i < 2; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    device uint* b = data;
    uint bytes = prm.y;
    uint acc = tid;
    { uint i = (0u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (65536u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (131072u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (196608u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (262144u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (327680u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (393216u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (458752u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (524288u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (589824u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (655360u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (720896u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (786432u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (851968u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (917504u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (983040u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1048576u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1114112u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1179648u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1245184u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1310720u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1376256u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1441792u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1507328u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1572864u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1638400u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1703936u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1769472u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1835008u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1900544u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (1966080u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = (2031616u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    out[tid] = acc;
}
""",
        ["buf2_perlane_device_select"] = """
// bench group=buf_two_perlane_device variant=select threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint2 slow2(device uint* b, uint bytes, uint addr)
{
    return uint2(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u));
}
static inline uint2 load2_outlined(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 2u <= words - index)
    {
        return uint2(b[index], b[index + 1u]);
    }
    return slow2(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint2 load2_select(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint2 r = uint2(0u);
    for (int i = 0; i < 2; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    device uint* b = data;
    uint bytes = prm.y;
    uint acc = tid;
    { uint2 w = load2_select(b, bytes, (0u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (65536u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (131072u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (196608u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (262144u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (327680u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (393216u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (458752u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (524288u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (589824u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (655360u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (720896u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (786432u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (851968u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (917504u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (983040u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1048576u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1114112u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1179648u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1245184u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1310720u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1376256u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1441792u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1507328u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1572864u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1638400u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1703936u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1769472u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1835008u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1900544u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (1966080u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, (2031616u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; }
    out[tid] = acc;
}
""",
        ["buf2_uniform_constant_checked"] = """
// bench group=buf_two_uniform_constant variant=checked threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE_CONSTANT

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint2 slow2(constant uint* b, uint bytes, uint addr)
{
    return uint2(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u));
}
static inline uint2 load2_outlined(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 2u <= words - index)
    {
        return uint2(b[index], b[index + 1u]);
    }
    return slow2(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint2 load2_select(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint2 r = uint2(0u);
    for (int i = 0; i < 2; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    constant uint* b = cdata;
    uint bytes = prm.y;
    uint acc = tid;
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 0u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 48u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 96u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 144u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 192u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 240u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 288u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 336u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 384u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 432u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 480u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 528u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 576u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 624u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 672u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 720u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 768u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 816u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 864u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 912u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 960u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 1008u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 1056u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 1104u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 1152u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 1200u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 1248u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 1296u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 1344u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 1392u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 1440u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 1488u); acc ^= w.x; acc += w.y; }
    out[tid] = acc;
}
""",
        ["buf2_uniform_constant_outlined"] = """
// bench group=buf_two_uniform_constant variant=outlined threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE_CONSTANT

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint2 slow2(constant uint* b, uint bytes, uint addr)
{
    return uint2(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u));
}
static inline uint2 load2_outlined(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 2u <= words - index)
    {
        return uint2(b[index], b[index + 1u]);
    }
    return slow2(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint2 load2_select(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint2 r = uint2(0u);
    for (int i = 0; i < 2; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    constant uint* b = cdata;
    uint bytes = prm.y;
    uint acc = tid;
    { uint2 w = load2_outlined(b, bytes, 0u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 48u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 96u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 144u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 192u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 240u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 288u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 336u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 384u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 432u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 480u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 528u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 576u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 624u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 672u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 720u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 768u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 816u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 864u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 912u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 960u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 1008u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 1056u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 1104u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 1152u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 1200u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 1248u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 1296u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 1344u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 1392u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 1440u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 1488u); acc ^= w.x; acc += w.y; }
    out[tid] = acc;
}
""",
        ["buf2_uniform_constant_raw"] = """
// bench group=buf_two_uniform_constant variant=raw threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE_CONSTANT

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint2 slow2(constant uint* b, uint bytes, uint addr)
{
    return uint2(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u));
}
static inline uint2 load2_outlined(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 2u <= words - index)
    {
        return uint2(b[index], b[index + 1u]);
    }
    return slow2(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint2 load2_select(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint2 r = uint2(0u);
    for (int i = 0; i < 2; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    constant uint* b = cdata;
    uint bytes = prm.y;
    uint acc = tid;
    { uint i = 0u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 48u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 96u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 144u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 192u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 240u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 288u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 336u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 384u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 432u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 480u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 528u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 576u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 624u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 672u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 720u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 768u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 816u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 864u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 912u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 960u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 1008u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 1056u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 1104u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 1152u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 1200u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 1248u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 1296u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 1344u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 1392u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 1440u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 1488u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    out[tid] = acc;
}
""",
        ["buf2_uniform_constant_select"] = """
// bench group=buf_two_uniform_constant variant=select threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE_CONSTANT

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint2 slow2(constant uint* b, uint bytes, uint addr)
{
    return uint2(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u));
}
static inline uint2 load2_outlined(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 2u <= words - index)
    {
        return uint2(b[index], b[index + 1u]);
    }
    return slow2(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint2 load2_select(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint2 r = uint2(0u);
    for (int i = 0; i < 2; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    constant uint* b = cdata;
    uint bytes = prm.y;
    uint acc = tid;
    { uint2 w = load2_select(b, bytes, 0u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 48u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 96u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 144u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 192u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 240u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 288u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 336u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 384u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 432u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 480u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 528u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 576u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 624u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 672u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 720u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 768u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 816u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 864u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 912u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 960u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 1008u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 1056u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 1104u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 1152u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 1200u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 1248u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 1296u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 1344u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 1392u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 1440u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 1488u); acc ^= w.x; acc += w.y; }
    out[tid] = acc;
}
""",
        ["buf2_uniform_device_checked"] = """
// bench group=buf_two_uniform_device variant=checked threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint2 slow2(device uint* b, uint bytes, uint addr)
{
    return uint2(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u));
}
static inline uint2 load2_outlined(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 2u <= words - index)
    {
        return uint2(b[index], b[index + 1u]);
    }
    return slow2(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint2 load2_select(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint2 r = uint2(0u);
    for (int i = 0; i < 2; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    device uint* b = data;
    uint bytes = prm.y;
    uint acc = tid;
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 0u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 48u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 96u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 144u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 192u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 240u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 288u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 336u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 384u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 432u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 480u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 528u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 576u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 624u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 672u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 720u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 768u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 816u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 864u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 912u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 960u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 1008u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 1056u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 1104u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 1152u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 1200u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 1248u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 1296u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 1344u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 1392u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 1440u); acc ^= w.x; acc += w.y; }
    { uint4 w = sharpemu_load_dwords<2>(b, bytes, 1488u); acc ^= w.x; acc += w.y; }
    out[tid] = acc;
}
""",
        ["buf2_uniform_device_outlined"] = """
// bench group=buf_two_uniform_device variant=outlined threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint2 slow2(device uint* b, uint bytes, uint addr)
{
    return uint2(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u));
}
static inline uint2 load2_outlined(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 2u <= words - index)
    {
        return uint2(b[index], b[index + 1u]);
    }
    return slow2(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint2 load2_select(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint2 r = uint2(0u);
    for (int i = 0; i < 2; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    device uint* b = data;
    uint bytes = prm.y;
    uint acc = tid;
    { uint2 w = load2_outlined(b, bytes, 0u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 48u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 96u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 144u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 192u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 240u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 288u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 336u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 384u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 432u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 480u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 528u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 576u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 624u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 672u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 720u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 768u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 816u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 864u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 912u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 960u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 1008u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 1056u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 1104u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 1152u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 1200u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 1248u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 1296u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 1344u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 1392u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 1440u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_outlined(b, bytes, 1488u); acc ^= w.x; acc += w.y; }
    out[tid] = acc;
}
""",
        ["buf2_uniform_device_raw"] = """
// bench group=buf_two_uniform_device variant=raw threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint2 slow2(device uint* b, uint bytes, uint addr)
{
    return uint2(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u));
}
static inline uint2 load2_outlined(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 2u <= words - index)
    {
        return uint2(b[index], b[index + 1u]);
    }
    return slow2(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint2 load2_select(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint2 r = uint2(0u);
    for (int i = 0; i < 2; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    device uint* b = data;
    uint bytes = prm.y;
    uint acc = tid;
    { uint i = 0u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 48u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 96u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 144u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 192u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 240u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 288u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 336u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 384u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 432u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 480u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 528u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 576u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 624u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 672u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 720u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 768u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 816u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 864u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 912u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 960u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 1008u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 1056u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 1104u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 1152u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 1200u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 1248u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 1296u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 1344u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 1392u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 1440u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    { uint i = 1488u >> 2; acc ^= b[i]; acc += b[i + 1u]; }
    out[tid] = acc;
}
""",
        ["buf2_uniform_device_select"] = """
// bench group=buf_two_uniform_device variant=select threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint2 slow2(device uint* b, uint bytes, uint addr)
{
    return uint2(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u));
}
static inline uint2 load2_outlined(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 2u <= words - index)
    {
        return uint2(b[index], b[index + 1u]);
    }
    return slow2(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint2 load2_select(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint2 r = uint2(0u);
    for (int i = 0; i < 2; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    device uint* b = data;
    uint bytes = prm.y;
    uint acc = tid;
    { uint2 w = load2_select(b, bytes, 0u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 48u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 96u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 144u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 192u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 240u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 288u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 336u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 384u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 432u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 480u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 528u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 576u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 624u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 672u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 720u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 768u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 816u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 864u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 912u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 960u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 1008u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 1056u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 1104u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 1152u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 1200u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 1248u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 1296u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 1344u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 1392u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 1440u); acc ^= w.x; acc += w.y; }
    { uint2 w = load2_select(b, bytes, 1488u); acc ^= w.x; acc += w.y; }
    out[tid] = acc;
}
""",
        ["buf_perlane_constant_checked"] = """
// bench group=buf_perlane_constant variant=checked threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE_CONSTANT

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint4 slow4(constant uint* b, uint bytes, uint addr)
{
    return uint4(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u),
                 sharpemu_load_dword(b, bytes, addr + 8u), sharpemu_load_dword(b, bytes, addr + 12u));
}
static inline uint4 load4_outlined(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 4u <= words - index)
    {
        return uint4(b[index], b[index + 1u], b[index + 2u], b[index + 3u]);
    }
    return slow4(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint4 load4_select(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint4 r = uint4(0u);
    for (int i = 0; i < 4; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    constant uint* b = cdata;
    uint bytes = prm.y;
    uint acc = tid;
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (0u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (65536u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (131072u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (196608u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (262144u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (327680u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (393216u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (458752u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (524288u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (589824u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (655360u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (720896u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (786432u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (851968u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (917504u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (983040u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1048576u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1114112u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1179648u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1245184u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1310720u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1376256u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1441792u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1507328u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1572864u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1638400u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1703936u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1769472u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1835008u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1900544u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1966080u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (2031616u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    out[tid] = acc;
}
""",
        ["buf_perlane_constant_outlined"] = """
// bench group=buf_perlane_constant variant=outlined threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE_CONSTANT

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint4 slow4(constant uint* b, uint bytes, uint addr)
{
    return uint4(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u),
                 sharpemu_load_dword(b, bytes, addr + 8u), sharpemu_load_dword(b, bytes, addr + 12u));
}
static inline uint4 load4_outlined(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 4u <= words - index)
    {
        return uint4(b[index], b[index + 1u], b[index + 2u], b[index + 3u]);
    }
    return slow4(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint4 load4_select(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint4 r = uint4(0u);
    for (int i = 0; i < 4; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    constant uint* b = cdata;
    uint bytes = prm.y;
    uint acc = tid;
    { uint4 w = load4_outlined(b, bytes, (0u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (65536u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (131072u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (196608u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (262144u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (327680u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (393216u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (458752u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (524288u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (589824u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (655360u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (720896u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (786432u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (851968u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (917504u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (983040u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1048576u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1114112u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1179648u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1245184u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1310720u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1376256u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1441792u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1507328u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1572864u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1638400u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1703936u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1769472u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1835008u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1900544u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1966080u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (2031616u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    out[tid] = acc;
}
""",
        ["buf_perlane_constant_raw"] = """
// bench group=buf_perlane_constant variant=raw threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE_CONSTANT

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint4 slow4(constant uint* b, uint bytes, uint addr)
{
    return uint4(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u),
                 sharpemu_load_dword(b, bytes, addr + 8u), sharpemu_load_dword(b, bytes, addr + 12u));
}
static inline uint4 load4_outlined(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 4u <= words - index)
    {
        return uint4(b[index], b[index + 1u], b[index + 2u], b[index + 3u]);
    }
    return slow4(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint4 load4_select(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint4 r = uint4(0u);
    for (int i = 0; i < 4; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    constant uint* b = cdata;
    uint bytes = prm.y;
    uint acc = tid;
    { uint i = (0u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (65536u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (131072u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (196608u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (262144u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (327680u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (393216u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (458752u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (524288u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (589824u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (655360u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (720896u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (786432u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (851968u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (917504u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (983040u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1048576u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1114112u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1179648u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1245184u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1310720u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1376256u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1441792u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1507328u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1572864u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1638400u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1703936u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1769472u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1835008u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1900544u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1966080u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (2031616u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    out[tid] = acc;
}
""",
        ["buf_perlane_constant_select"] = """
// bench group=buf_perlane_constant variant=select threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE_CONSTANT

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint4 slow4(constant uint* b, uint bytes, uint addr)
{
    return uint4(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u),
                 sharpemu_load_dword(b, bytes, addr + 8u), sharpemu_load_dword(b, bytes, addr + 12u));
}
static inline uint4 load4_outlined(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 4u <= words - index)
    {
        return uint4(b[index], b[index + 1u], b[index + 2u], b[index + 3u]);
    }
    return slow4(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint4 load4_select(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint4 r = uint4(0u);
    for (int i = 0; i < 4; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    constant uint* b = cdata;
    uint bytes = prm.y;
    uint acc = tid;
    { uint4 w = load4_select(b, bytes, (0u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (65536u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (131072u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (196608u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (262144u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (327680u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (393216u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (458752u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (524288u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (589824u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (655360u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (720896u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (786432u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (851968u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (917504u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (983040u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1048576u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1114112u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1179648u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1245184u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1310720u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1376256u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1441792u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1507328u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1572864u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1638400u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1703936u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1769472u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1835008u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1900544u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1966080u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (2031616u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    out[tid] = acc;
}
""",
        ["buf_perlane_device_checked"] = """
// bench group=buf_perlane_device variant=checked threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint4 slow4(device uint* b, uint bytes, uint addr)
{
    return uint4(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u),
                 sharpemu_load_dword(b, bytes, addr + 8u), sharpemu_load_dword(b, bytes, addr + 12u));
}
static inline uint4 load4_outlined(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 4u <= words - index)
    {
        return uint4(b[index], b[index + 1u], b[index + 2u], b[index + 3u]);
    }
    return slow4(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint4 load4_select(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint4 r = uint4(0u);
    for (int i = 0; i < 4; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    device uint* b = data;
    uint bytes = prm.y;
    uint acc = tid;
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (0u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (65536u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (131072u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (196608u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (262144u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (327680u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (393216u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (458752u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (524288u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (589824u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (655360u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (720896u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (786432u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (851968u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (917504u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (983040u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1048576u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1114112u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1179648u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1245184u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1310720u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1376256u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1441792u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1507328u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1572864u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1638400u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1703936u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1769472u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1835008u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1900544u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (1966080u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, (2031616u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    out[tid] = acc;
}
""",
        ["buf_perlane_device_outlined"] = """
// bench group=buf_perlane_device variant=outlined threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint4 slow4(device uint* b, uint bytes, uint addr)
{
    return uint4(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u),
                 sharpemu_load_dword(b, bytes, addr + 8u), sharpemu_load_dword(b, bytes, addr + 12u));
}
static inline uint4 load4_outlined(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 4u <= words - index)
    {
        return uint4(b[index], b[index + 1u], b[index + 2u], b[index + 3u]);
    }
    return slow4(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint4 load4_select(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint4 r = uint4(0u);
    for (int i = 0; i < 4; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    device uint* b = data;
    uint bytes = prm.y;
    uint acc = tid;
    { uint4 w = load4_outlined(b, bytes, (0u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (65536u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (131072u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (196608u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (262144u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (327680u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (393216u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (458752u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (524288u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (589824u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (655360u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (720896u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (786432u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (851968u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (917504u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (983040u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1048576u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1114112u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1179648u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1245184u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1310720u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1376256u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1441792u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1507328u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1572864u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1638400u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1703936u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1769472u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1835008u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1900544u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (1966080u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, (2031616u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    out[tid] = acc;
}
""",
        ["buf_perlane_device_raw"] = """
// bench group=buf_perlane_device variant=raw threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint4 slow4(device uint* b, uint bytes, uint addr)
{
    return uint4(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u),
                 sharpemu_load_dword(b, bytes, addr + 8u), sharpemu_load_dword(b, bytes, addr + 12u));
}
static inline uint4 load4_outlined(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 4u <= words - index)
    {
        return uint4(b[index], b[index + 1u], b[index + 2u], b[index + 3u]);
    }
    return slow4(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint4 load4_select(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint4 r = uint4(0u);
    for (int i = 0; i < 4; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    device uint* b = data;
    uint bytes = prm.y;
    uint acc = tid;
    { uint i = (0u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (65536u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (131072u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (196608u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (262144u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (327680u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (393216u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (458752u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (524288u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (589824u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (655360u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (720896u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (786432u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (851968u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (917504u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (983040u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1048576u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1114112u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1179648u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1245184u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1310720u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1376256u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1441792u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1507328u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1572864u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1638400u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1703936u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1769472u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1835008u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1900544u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (1966080u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = (2031616u + ((tid * 16u) & 0xFFFFu)) >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    out[tid] = acc;
}
""",
        ["buf_perlane_device_select"] = """
// bench group=buf_perlane_device variant=select threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint4 slow4(device uint* b, uint bytes, uint addr)
{
    return uint4(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u),
                 sharpemu_load_dword(b, bytes, addr + 8u), sharpemu_load_dword(b, bytes, addr + 12u));
}
static inline uint4 load4_outlined(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 4u <= words - index)
    {
        return uint4(b[index], b[index + 1u], b[index + 2u], b[index + 3u]);
    }
    return slow4(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint4 load4_select(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint4 r = uint4(0u);
    for (int i = 0; i < 4; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    device uint* b = data;
    uint bytes = prm.y;
    uint acc = tid;
    { uint4 w = load4_select(b, bytes, (0u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (65536u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (131072u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (196608u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (262144u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (327680u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (393216u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (458752u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (524288u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (589824u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (655360u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (720896u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (786432u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (851968u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (917504u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (983040u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1048576u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1114112u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1179648u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1245184u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1310720u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1376256u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1441792u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1507328u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1572864u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1638400u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1703936u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1769472u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1835008u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1900544u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (1966080u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, (2031616u + ((tid * 16u) & 0xFFFFu))); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    out[tid] = acc;
}
""",
        ["buf_uniform_constant_checked"] = """
// bench group=buf_uniform_constant variant=checked threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE_CONSTANT

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint4 slow4(constant uint* b, uint bytes, uint addr)
{
    return uint4(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u),
                 sharpemu_load_dword(b, bytes, addr + 8u), sharpemu_load_dword(b, bytes, addr + 12u));
}
static inline uint4 load4_outlined(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 4u <= words - index)
    {
        return uint4(b[index], b[index + 1u], b[index + 2u], b[index + 3u]);
    }
    return slow4(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint4 load4_select(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint4 r = uint4(0u);
    for (int i = 0; i < 4; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    constant uint* b = cdata;
    uint bytes = prm.y;
    uint acc = tid;
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 0u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 48u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 96u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 144u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 192u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 240u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 288u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 336u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 384u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 432u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 480u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 528u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 576u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 624u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 672u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 720u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 768u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 816u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 864u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 912u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 960u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 1008u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 1056u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 1104u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 1152u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 1200u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 1248u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 1296u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 1344u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 1392u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 1440u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 1488u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    out[tid] = acc;
}
""",
        ["buf_uniform_constant_outlined"] = """
// bench group=buf_uniform_constant variant=outlined threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE_CONSTANT

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint4 slow4(constant uint* b, uint bytes, uint addr)
{
    return uint4(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u),
                 sharpemu_load_dword(b, bytes, addr + 8u), sharpemu_load_dword(b, bytes, addr + 12u));
}
static inline uint4 load4_outlined(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 4u <= words - index)
    {
        return uint4(b[index], b[index + 1u], b[index + 2u], b[index + 3u]);
    }
    return slow4(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint4 load4_select(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint4 r = uint4(0u);
    for (int i = 0; i < 4; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    constant uint* b = cdata;
    uint bytes = prm.y;
    uint acc = tid;
    { uint4 w = load4_outlined(b, bytes, 0u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 48u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 96u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 144u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 192u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 240u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 288u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 336u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 384u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 432u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 480u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 528u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 576u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 624u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 672u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 720u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 768u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 816u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 864u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 912u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 960u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 1008u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 1056u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 1104u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 1152u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 1200u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 1248u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 1296u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 1344u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 1392u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 1440u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 1488u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    out[tid] = acc;
}
""",
        ["buf_uniform_constant_raw"] = """
// bench group=buf_uniform_constant variant=raw threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE_CONSTANT

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint4 slow4(constant uint* b, uint bytes, uint addr)
{
    return uint4(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u),
                 sharpemu_load_dword(b, bytes, addr + 8u), sharpemu_load_dword(b, bytes, addr + 12u));
}
static inline uint4 load4_outlined(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 4u <= words - index)
    {
        return uint4(b[index], b[index + 1u], b[index + 2u], b[index + 3u]);
    }
    return slow4(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint4 load4_select(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint4 r = uint4(0u);
    for (int i = 0; i < 4; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    constant uint* b = cdata;
    uint bytes = prm.y;
    uint acc = tid;
    { uint i = 0u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 48u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 96u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 144u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 192u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 240u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 288u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 336u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 384u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 432u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 480u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 528u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 576u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 624u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 672u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 720u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 768u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 816u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 864u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 912u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 960u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 1008u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 1056u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 1104u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 1152u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 1200u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 1248u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 1296u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 1344u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 1392u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 1440u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 1488u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    out[tid] = acc;
}
""",
        ["buf_uniform_constant_select"] = """
// bench group=buf_uniform_constant variant=select threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE_CONSTANT

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint4 slow4(constant uint* b, uint bytes, uint addr)
{
    return uint4(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u),
                 sharpemu_load_dword(b, bytes, addr + 8u), sharpemu_load_dword(b, bytes, addr + 12u));
}
static inline uint4 load4_outlined(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 4u <= words - index)
    {
        return uint4(b[index], b[index + 1u], b[index + 2u], b[index + 3u]);
    }
    return slow4(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint4 load4_select(constant uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint4 r = uint4(0u);
    for (int i = 0; i < 4; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    constant uint* b = cdata;
    uint bytes = prm.y;
    uint acc = tid;
    { uint4 w = load4_select(b, bytes, 0u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 48u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 96u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 144u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 192u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 240u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 288u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 336u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 384u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 432u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 480u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 528u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 576u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 624u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 672u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 720u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 768u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 816u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 864u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 912u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 960u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 1008u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 1056u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 1104u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 1152u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 1200u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 1248u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 1296u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 1344u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 1392u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 1440u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 1488u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    out[tid] = acc;
}
""",
        ["buf_uniform_device_checked"] = """
// bench group=buf_uniform_device variant=checked threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint4 slow4(device uint* b, uint bytes, uint addr)
{
    return uint4(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u),
                 sharpemu_load_dword(b, bytes, addr + 8u), sharpemu_load_dword(b, bytes, addr + 12u));
}
static inline uint4 load4_outlined(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 4u <= words - index)
    {
        return uint4(b[index], b[index + 1u], b[index + 2u], b[index + 3u]);
    }
    return slow4(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint4 load4_select(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint4 r = uint4(0u);
    for (int i = 0; i < 4; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    device uint* b = data;
    uint bytes = prm.y;
    uint acc = tid;
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 0u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 48u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 96u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 144u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 192u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 240u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 288u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 336u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 384u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 432u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 480u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 528u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 576u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 624u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 672u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 720u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 768u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 816u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 864u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 912u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 960u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 1008u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 1056u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 1104u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 1152u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 1200u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 1248u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 1296u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 1344u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 1392u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 1440u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = sharpemu_load_dwords<4>(b, bytes, 1488u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    out[tid] = acc;
}
""",
        ["buf_uniform_device_outlined"] = """
// bench group=buf_uniform_device variant=outlined threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint4 slow4(device uint* b, uint bytes, uint addr)
{
    return uint4(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u),
                 sharpemu_load_dword(b, bytes, addr + 8u), sharpemu_load_dword(b, bytes, addr + 12u));
}
static inline uint4 load4_outlined(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 4u <= words - index)
    {
        return uint4(b[index], b[index + 1u], b[index + 2u], b[index + 3u]);
    }
    return slow4(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint4 load4_select(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint4 r = uint4(0u);
    for (int i = 0; i < 4; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    device uint* b = data;
    uint bytes = prm.y;
    uint acc = tid;
    { uint4 w = load4_outlined(b, bytes, 0u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 48u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 96u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 144u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 192u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 240u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 288u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 336u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 384u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 432u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 480u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 528u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 576u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 624u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 672u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 720u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 768u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 816u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 864u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 912u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 960u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 1008u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 1056u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 1104u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 1152u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 1200u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 1248u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 1296u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 1344u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 1392u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 1440u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_outlined(b, bytes, 1488u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    out[tid] = acc;
}
""",
        ["buf_uniform_device_raw"] = """
// bench group=buf_uniform_device variant=raw threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint4 slow4(device uint* b, uint bytes, uint addr)
{
    return uint4(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u),
                 sharpemu_load_dword(b, bytes, addr + 8u), sharpemu_load_dword(b, bytes, addr + 12u));
}
static inline uint4 load4_outlined(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 4u <= words - index)
    {
        return uint4(b[index], b[index + 1u], b[index + 2u], b[index + 3u]);
    }
    return slow4(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint4 load4_select(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint4 r = uint4(0u);
    for (int i = 0; i < 4; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    device uint* b = data;
    uint bytes = prm.y;
    uint acc = tid;
    { uint i = 0u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 48u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 96u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 144u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 192u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 240u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 288u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 336u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 384u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 432u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 480u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 528u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 576u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 624u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 672u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 720u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 768u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 816u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 864u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 912u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 960u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 1008u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 1056u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 1104u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 1152u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 1200u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 1248u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 1296u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 1344u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 1392u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 1440u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    { uint i = 1488u >> 2; acc ^= b[i]; acc += b[i + 1u]; acc ^= b[i + 2u]; acc += b[i + 3u]; }
    out[tid] = acc;
}
""",
        ["buf_uniform_device_select"] = """
// bench group=buf_uniform_device variant=select threads=1048576 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@BUF_PRELUDE

// Fast path inline, the dword-by-dword path out of line.
__attribute__((noinline)) static uint4 slow4(device uint* b, uint bytes, uint addr)
{
    return uint4(sharpemu_load_dword(b, bytes, addr), sharpemu_load_dword(b, bytes, addr + 4u),
                 sharpemu_load_dword(b, bytes, addr + 8u), sharpemu_load_dword(b, bytes, addr + 12u));
}
static inline uint4 load4_outlined(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    if (index < words && 4u <= words - index)
    {
        return uint4(b[index], b[index + 1u], b[index + 2u], b[index + 3u]);
    }
    return slow4(b, bytes, addr);
}
// Branch-free: every dword is read at an index clamped into the binding and selected against
// its own bounds test, which is what sharpemu_load_dword answers per dword. Needs a binding of
// at least one dword.
static inline uint4 load4_select(device uint* b, uint bytes, uint addr)
{
    uint index = addr >> 2;
    uint words = bytes >> 2;
    uint last = max(words, 1u) - 1u;
    uint4 r = uint4(0u);
    for (int i = 0; i < 4; i++)
    {
        uint ix = ((addr >> 2) + (uint)i) & 0x3FFFFFFFu;
        uint v = b[ix > last ? last : ix];
        r[i] = ix < words ? v : 0u;
    }
    return r;
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* cdata [[buffer(5)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    device uint* b = data;
    uint bytes = prm.y;
    uint acc = tid;
    { uint4 w = load4_select(b, bytes, 0u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 48u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 96u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 144u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 192u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 240u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 288u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 336u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 384u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 432u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 480u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 528u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 576u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 624u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 672u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 720u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 768u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 816u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 864u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 912u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 960u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 1008u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 1056u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 1104u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 1152u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 1200u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 1248u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 1296u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 1344u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 1392u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 1440u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { uint4 w = load4_select(b, bytes, 1488u); acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    out[tid] = acc;
}
""",
        ["dev_altranges_direct"] = """
// bench group=dev_altranges variant=direct threads=262144 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@DEV_PRELUDE

kernel void bench(
    device uint* data [[buffer(0)]],
    device uint* out [[buffer(1)]],
    constant uint4& prm [[buffer(2)]],
    device const Gen5AddressRange* sharpemu_ranges [[buffer(3)]],
    device uint* sharpemu_faults [[buffer(4)]],
    uint tid [[thread_position_in_grid]],
    uint lane [[thread_index_in_simdgroup]],
    uint tgi [[thread_index_in_threadgroup]])

{
    uint range_count = prm.w;
    Gen5RangeCache cache = { 0ul, 0ul, nullptr };
    ulong gA = ((ulong)prm.z << 32) | (ulong)prm.y;
    ulong gB = gA + 0x800000ul;
    uint acc = tid;
    { ulong a = (gA + 0ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 0ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gB + 0ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0x200000) + 0ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 131072ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 131072ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gB + 131072ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0x200000) + 131072ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 262144ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 262144ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gB + 262144ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0x200000) + 262144ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 393216ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 393216ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gB + 393216ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0x200000) + 393216ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 524288ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 524288ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gB + 524288ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0x200000) + 524288ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 655360ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 655360ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gB + 655360ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0x200000) + 655360ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 786432ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 786432ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gB + 786432ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0x200000) + 786432ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 917504ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 917504ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gB + 917504ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0x200000) + 917504ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 1048576ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 1048576ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gB + 1048576ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0x200000) + 1048576ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 1179648ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 1179648ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gB + 1179648ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0x200000) + 1179648ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 1310720ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 1310720ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gB + 1310720ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0x200000) + 1310720ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 1441792ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 1441792ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gB + 1441792ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0x200000) + 1441792ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 1572864ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 1572864ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gB + 1572864ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0x200000) + 1572864ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 1703936ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 1703936ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gB + 1703936ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0x200000) + 1703936ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 1835008ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 1835008ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gB + 1835008ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0x200000) + 1835008ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 1966080ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 1966080ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gB + 1966080ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0x200000) + 1966080ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    out[tid] = acc;
}
""",
        ["dev_altranges_perdword"] = """
// bench group=dev_altranges variant=perdword threads=262144 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@DEV_PRELUDE

kernel void bench(
    device uint* data [[buffer(0)]],
    device uint* out [[buffer(1)]],
    constant uint4& prm [[buffer(2)]],
    device const Gen5AddressRange* sharpemu_ranges [[buffer(3)]],
    device uint* sharpemu_faults [[buffer(4)]],
    uint tid [[thread_position_in_grid]],
    uint lane [[thread_index_in_simdgroup]],
    uint tgi [[thread_index_in_threadgroup]])

{
    uint range_count = prm.w;
    Gen5RangeCache cache = { 0ul, 0ul, nullptr };
    ulong gA = ((ulong)prm.z << 32) | (ulong)prm.y;
    ulong gB = gA + 0x800000ul;
    uint acc = tid;
    { ulong a = (gA + 0ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gB + 0ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 131072ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gB + 131072ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 262144ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gB + 262144ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 393216ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gB + 393216ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 524288ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gB + 524288ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 655360ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gB + 655360ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 786432ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gB + 786432ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 917504ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gB + 917504ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 1048576ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gB + 1048576ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 1179648ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gB + 1179648ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 1310720ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gB + 1310720ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 1441792ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gB + 1441792ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 1572864ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gB + 1572864ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 1703936ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gB + 1703936ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 1835008ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gB + 1835008ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 1966080ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gB + 1966080ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    out[tid] = acc;
}
""",
        ["dev_altranges_span_cache2"] = """
// bench group=dev_altranges variant=span_cache2 threads=262144 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@DEV_PRELUDE
struct Cache2 { Gen5RangeCache c0; Gen5RangeCache c1; };
static inline device uint* resolve2(device const Gen5AddressRange* ranges, uint range_count, device uint* faults, thread Cache2& c, ulong address, thread bool& valid)
{
    ulong masked = address & 0xFFFFFFFFFFFFul;
    ulong page = masked >> 14u;
    if (page - c.c0.first_page < c.c0.page_count)
    {
        valid = true;
        return (device uint*)((device uchar*)c.c0.base + ((page - c.c0.first_page) << 14u) + (masked & 0x3FFFul));
    }
    if (page - c.c1.first_page < c.c1.page_count)
    {
        valid = true;
        Gen5RangeCache t = c.c0; c.c0 = c.c1; c.c1 = t;
        return (device uint*)((device uchar*)c.c0.base + ((page - c.c0.first_page) << 14u) + (masked & 0x3FFFul));
    }
    c.c1 = c.c0;
    return sharpemu_resolve_device_address(ranges, range_count, faults, 1024u, c.c0, address, valid);
}
kernel void bench(
    device uint* data [[buffer(0)]],
    device uint* out [[buffer(1)]],
    constant uint4& prm [[buffer(2)]],
    device const Gen5AddressRange* sharpemu_ranges [[buffer(3)]],
    device uint* sharpemu_faults [[buffer(4)]],
    uint tid [[thread_position_in_grid]],
    uint lane [[thread_index_in_simdgroup]],
    uint tgi [[thread_index_in_threadgroup]])

{
    uint range_count = prm.w;
    Cache2 c2 = { { 0ul, 0ul, nullptr }, { 0ul, 0ul, nullptr } };
    ulong gA = ((ulong)prm.z << 32) | (ulong)prm.y;
    ulong gB = gA + 0x800000ul;
    uint acc = tid;
    { ulong a = (gA + 0ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 0ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 131072ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 131072ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 262144ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 262144ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 393216ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 393216ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 524288ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 524288ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 655360ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 655360ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 786432ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 786432ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 917504ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 917504ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1048576ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 1048576ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1179648ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 1179648ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1310720ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 1310720ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1441792ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 1441792ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1572864ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 1572864ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1703936ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 1703936ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1835008ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 1835008ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1966080ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 1966080ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    out[tid] = acc;
}
""",
        ["dev_altranges_span_inline"] = """
// bench group=dev_altranges variant=span_inline threads=262144 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@DEV_PRELUDE

kernel void bench(
    device uint* data [[buffer(0)]],
    device uint* out [[buffer(1)]],
    constant uint4& prm [[buffer(2)]],
    device const Gen5AddressRange* sharpemu_ranges [[buffer(3)]],
    device uint* sharpemu_faults [[buffer(4)]],
    uint tid [[thread_position_in_grid]],
    uint lane [[thread_index_in_simdgroup]],
    uint tgi [[thread_index_in_threadgroup]])

{
    uint range_count = prm.w;
    Gen5RangeCache cache = { 0ul, 0ul, nullptr };
    ulong gA = ((ulong)prm.z << 32) | (ulong)prm.y;
    ulong gB = gA + 0x800000ul;
    uint acc = tid;
    { ulong a = (gA + 0ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 0ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 131072ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 131072ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 262144ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 262144ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 393216ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 393216ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 524288ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 524288ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 655360ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 655360ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 786432ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 786432ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 917504ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 917504ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1048576ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 1048576ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1179648ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 1179648ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1310720ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 1310720ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1441792ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 1441792ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1572864ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 1572864ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1703936ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 1703936ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1835008ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 1835008ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1966080ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gB + 1966080ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    out[tid] = acc;
}
""",
        ["dev_altranges_span_outlined"] = """
// bench group=dev_altranges variant=span_outlined threads=262144 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@DEV_PRELUDE

__attribute__((noinline)) static uint4 slow4(device const Gen5AddressRange* r, uint rc, device uint* f, thread Gen5RangeCache& cache, ulong a)
{
    return uint4(sharpemu_load_device_dword(r, rc, f, 1024u, cache, a), sharpemu_load_device_dword(r, rc, f, 1024u, cache, a + 4ul),
                 sharpemu_load_device_dword(r, rc, f, 1024u, cache, a + 8ul), sharpemu_load_device_dword(r, rc, f, 1024u, cache, a + 12ul));
}

kernel void bench(
    device uint* data [[buffer(0)]],
    device uint* out [[buffer(1)]],
    constant uint4& prm [[buffer(2)]],
    device const Gen5AddressRange* sharpemu_ranges [[buffer(3)]],
    device uint* sharpemu_faults [[buffer(4)]],
    uint tid [[thread_position_in_grid]],
    uint lane [[thread_index_in_simdgroup]],
    uint tgi [[thread_index_in_threadgroup]])

{
    uint range_count = prm.w;
    Gen5RangeCache cache = { 0ul, 0ul, nullptr };
    ulong gA = ((ulong)prm.z << 32) | (ulong)prm.y;
    ulong gB = gA + 0x800000ul;
    uint acc = tid;
    { ulong a = (gA + 0ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gB + 0ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 131072ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gB + 131072ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 262144ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gB + 262144ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 393216ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gB + 393216ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 524288ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gB + 524288ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 655360ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gB + 655360ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 786432ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gB + 786432ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 917504ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gB + 917504ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 1048576ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gB + 1048576ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 1179648ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gB + 1179648ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 1310720ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gB + 1310720ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 1441792ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gB + 1441792ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 1572864ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gB + 1572864ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 1703936ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gB + 1703936ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 1835008ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gB + 1835008ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 1966080ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gB + 1966080ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    out[tid] = acc;
}
""",
        ["dev_onerange_direct"] = """
// bench group=dev_onerange variant=direct threads=262144 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@DEV_PRELUDE

kernel void bench(
    device uint* data [[buffer(0)]],
    device uint* out [[buffer(1)]],
    constant uint4& prm [[buffer(2)]],
    device const Gen5AddressRange* sharpemu_ranges [[buffer(3)]],
    device uint* sharpemu_faults [[buffer(4)]],
    uint tid [[thread_position_in_grid]],
    uint lane [[thread_index_in_simdgroup]],
    uint tgi [[thread_index_in_threadgroup]])

{
    uint range_count = prm.w;
    Gen5RangeCache cache = { 0ul, 0ul, nullptr };
    ulong gA = ((ulong)prm.z << 32) | (ulong)prm.y;
    ulong gB = gA + 0x800000ul;
    uint acc = tid;
    { ulong a = (gA + 0ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 0ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 131072ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 131072ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 262144ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 262144ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 393216ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 393216ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 524288ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 524288ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 655360ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 655360ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 786432ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 786432ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 917504ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 917504ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 1048576ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 1048576ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 1179648ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 1179648ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 1310720ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 1310720ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 1441792ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 1441792ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 1572864ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 1572864ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 1703936ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 1703936ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 1835008ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 1835008ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 1966080ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 1966080ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 2097152ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 2097152ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 2228224ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 2228224ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 2359296ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 2359296ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 2490368ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 2490368ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 2621440ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 2621440ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 2752512ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 2752512ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 2883584ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 2883584ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 3014656ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 3014656ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 3145728ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 3145728ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 3276800ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 3276800ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 3407872ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 3407872ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 3538944ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 3538944ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 3670016ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 3670016ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 3801088ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 3801088ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 3932160ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 3932160ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    { ulong a = (gA + 4063232ul + (ulong)((tid * 16u) & 0x1FFFFu));
      device uint* p = (device uint*)((device uchar*)(data + 0) + 4063232ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= p[0]; acc += p[1]; acc ^= p[2]; acc += p[3]; }
    out[tid] = acc;
}
""",
        ["dev_onerange_perdword"] = """
// bench group=dev_onerange variant=perdword threads=262144 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@DEV_PRELUDE

kernel void bench(
    device uint* data [[buffer(0)]],
    device uint* out [[buffer(1)]],
    constant uint4& prm [[buffer(2)]],
    device const Gen5AddressRange* sharpemu_ranges [[buffer(3)]],
    device uint* sharpemu_faults [[buffer(4)]],
    uint tid [[thread_position_in_grid]],
    uint lane [[thread_index_in_simdgroup]],
    uint tgi [[thread_index_in_threadgroup]])

{
    uint range_count = prm.w;
    Gen5RangeCache cache = { 0ul, 0ul, nullptr };
    ulong gA = ((ulong)prm.z << 32) | (ulong)prm.y;
    ulong gB = gA + 0x800000ul;
    uint acc = tid;
    { ulong a = (gA + 0ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 131072ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 262144ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 393216ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 524288ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 655360ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 786432ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 917504ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 1048576ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 1179648ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 1310720ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 1441792ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 1572864ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 1703936ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 1835008ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 1966080ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 2097152ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 2228224ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 2359296ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 2490368ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 2621440ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 2752512ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 2883584ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 3014656ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 3145728ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 3276800ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 3407872ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 3538944ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 3670016ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 3801088ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 3932160ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    { ulong a = (gA + 4063232ul + (ulong)((tid * 16u) & 0x1FFFFu));
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      acc ^= sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul); acc += sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul); }
    out[tid] = acc;
}
""",
        ["dev_onerange_span_cache2"] = """
// bench group=dev_onerange variant=span_cache2 threads=262144 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@DEV_PRELUDE
struct Cache2 { Gen5RangeCache c0; Gen5RangeCache c1; };
static inline device uint* resolve2(device const Gen5AddressRange* ranges, uint range_count, device uint* faults, thread Cache2& c, ulong address, thread bool& valid)
{
    ulong masked = address & 0xFFFFFFFFFFFFul;
    ulong page = masked >> 14u;
    if (page - c.c0.first_page < c.c0.page_count)
    {
        valid = true;
        return (device uint*)((device uchar*)c.c0.base + ((page - c.c0.first_page) << 14u) + (masked & 0x3FFFul));
    }
    if (page - c.c1.first_page < c.c1.page_count)
    {
        valid = true;
        Gen5RangeCache t = c.c0; c.c0 = c.c1; c.c1 = t;
        return (device uint*)((device uchar*)c.c0.base + ((page - c.c0.first_page) << 14u) + (masked & 0x3FFFul));
    }
    c.c1 = c.c0;
    return sharpemu_resolve_device_address(ranges, range_count, faults, 1024u, c.c0, address, valid);
}
kernel void bench(
    device uint* data [[buffer(0)]],
    device uint* out [[buffer(1)]],
    constant uint4& prm [[buffer(2)]],
    device const Gen5AddressRange* sharpemu_ranges [[buffer(3)]],
    device uint* sharpemu_faults [[buffer(4)]],
    uint tid [[thread_position_in_grid]],
    uint lane [[thread_index_in_simdgroup]],
    uint tgi [[thread_index_in_threadgroup]])

{
    uint range_count = prm.w;
    Cache2 c2 = { { 0ul, 0ul, nullptr }, { 0ul, 0ul, nullptr } };
    ulong gA = ((ulong)prm.z << 32) | (ulong)prm.y;
    ulong gB = gA + 0x800000ul;
    uint acc = tid;
    { ulong a = (gA + 0ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 131072ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 262144ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 393216ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 524288ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 655360ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 786432ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 917504ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1048576ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1179648ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1310720ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1441792ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1572864ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1703936ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1835008ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1966080ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 2097152ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 2228224ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 2359296ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 2490368ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 2621440ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 2752512ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 2883584ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 3014656ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 3145728ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 3276800ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 3407872ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 3538944ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 3670016ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 3801088ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 3932160ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 4063232ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = resolve2(sharpemu_ranges, range_count, sharpemu_faults, c2, a, ok);
      ok = ok && (a & 0x3FFFul) + 16ul <= 0x4000ul;
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 0ul);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, c2.c0, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    out[tid] = acc;
}
""",
        ["dev_onerange_span_inline"] = """
// bench group=dev_onerange variant=span_inline threads=262144 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@DEV_PRELUDE

kernel void bench(
    device uint* data [[buffer(0)]],
    device uint* out [[buffer(1)]],
    constant uint4& prm [[buffer(2)]],
    device const Gen5AddressRange* sharpemu_ranges [[buffer(3)]],
    device uint* sharpemu_faults [[buffer(4)]],
    uint tid [[thread_position_in_grid]],
    uint lane [[thread_index_in_simdgroup]],
    uint tgi [[thread_index_in_threadgroup]])

{
    uint range_count = prm.w;
    Gen5RangeCache cache = { 0ul, 0ul, nullptr };
    ulong gA = ((ulong)prm.z << 32) | (ulong)prm.y;
    ulong gB = gA + 0x800000ul;
    uint acc = tid;
    { ulong a = (gA + 0ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 131072ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 262144ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 393216ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 524288ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 655360ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 786432ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 917504ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1048576ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1179648ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1310720ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1441792ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1572864ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1703936ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1835008ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 1966080ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 2097152ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 2228224ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 2359296ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 2490368ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 2621440ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 2752512ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 2883584ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 3014656ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 3145728ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 3276800ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 3407872ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 3538944ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 3670016ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 3801088ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 3932160ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    { ulong a = (gA + 4063232ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint w0 = ok ? p[0] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a);
      uint w1 = ok ? p[1] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 4ul);
      uint w2 = ok ? p[2] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 8ul);
      uint w3 = ok ? p[3] : sharpemu_load_device_dword(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a + 12ul);
      acc ^= w0; acc += w1; acc ^= w2; acc += w3; }
    out[tid] = acc;
}
""",
        ["dev_onerange_span_outlined"] = """
// bench group=dev_onerange variant=span_outlined threads=262144 tg=32 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
//@DEV_PRELUDE

__attribute__((noinline)) static uint4 slow4(device const Gen5AddressRange* r, uint rc, device uint* f, thread Gen5RangeCache& cache, ulong a)
{
    return uint4(sharpemu_load_device_dword(r, rc, f, 1024u, cache, a), sharpemu_load_device_dword(r, rc, f, 1024u, cache, a + 4ul),
                 sharpemu_load_device_dword(r, rc, f, 1024u, cache, a + 8ul), sharpemu_load_device_dword(r, rc, f, 1024u, cache, a + 12ul));
}

kernel void bench(
    device uint* data [[buffer(0)]],
    device uint* out [[buffer(1)]],
    constant uint4& prm [[buffer(2)]],
    device const Gen5AddressRange* sharpemu_ranges [[buffer(3)]],
    device uint* sharpemu_faults [[buffer(4)]],
    uint tid [[thread_position_in_grid]],
    uint lane [[thread_index_in_simdgroup]],
    uint tgi [[thread_index_in_threadgroup]])

{
    uint range_count = prm.w;
    Gen5RangeCache cache = { 0ul, 0ul, nullptr };
    ulong gA = ((ulong)prm.z << 32) | (ulong)prm.y;
    ulong gB = gA + 0x800000ul;
    uint acc = tid;
    { ulong a = (gA + 0ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 131072ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 262144ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 393216ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 524288ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 655360ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 786432ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 917504ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 1048576ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 1179648ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 1310720ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 1441792ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 1572864ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 1703936ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 1835008ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 1966080ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 2097152ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 2228224ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 2359296ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 2490368ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 2621440ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 2752512ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 2883584ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 3014656ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 3145728ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 3276800ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 3407872ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 3538944ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 3670016ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 3801088ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 3932160ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    { ulong a = (gA + 4063232ul + (ulong)((tid * 16u) & 0x1FFFFu));
      bool ok;
      device uint* p = sharpemu_resolve_device_span(sharpemu_ranges, range_count, sharpemu_faults, 1024u, cache, a, 16u, ok);
      uint4 w = ok ? uint4(p[0], p[1], p[2], p[3]) : slow4(sharpemu_ranges, range_count, sharpemu_faults, cache, a);
      acc ^= w.x; acc += w.y; acc ^= w.z; acc += w.w; }
    out[tid] = acc;
}
""",
        ["exec_guarded"] = """
// bench group=exec_guard variant=current threads=1048576 tg=32 iters=64 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    uint m = data[0];
    bool exec = ((m >> lane) & 1u) != 0u;
    uint v0 = (data[(tid * 3u) & 0x3FFFFFu] >> 9) | 0x3F800000u, v1 = (data[(tid * 5u + 1u) & 0x3FFFFFu] >> 9) | 0x3F800000u;
    uint v2 = (data[(tid * 7u + 2u) & 0x3FFFFFu] >> 9) | 0x3F800000u, v3 = (data[(tid * 11u + 3u) & 0x3FFFFFu] >> 9) | 0x3F800000u;
    uint v4 = v0 ^ 0x1234u, v5 = v1 ^ 0x4321u, v6 = v2 ^ 0x777u, v7 = v3 ^ 0x999u;
    uint s0 = 0x3F000000u | (data[1] & 0xFFFFu), s1 = 0x3F000000u | (data[2] & 0xFFFFu), s2 = 0x3E800000u | (data[3] & 0xFFFFu), s3 = 0x3F7F0000u;
    for (uint it = 0u; it < prm.x; it++)
    {
        { float t = as_type<float>(s0) * as_type<float>(v0); float u = as_type<float>(v1); uint r = as_type<uint>(t + u); if (exec) { v2 = r; } }
        { float t = as_type<float>(s1) * as_type<float>(v3); float u = as_type<float>(v6); uint r = as_type<uint>(t + u); if (exec) { v1 = r; } }
        { float t = as_type<float>(s2) * as_type<float>(v6); float u = as_type<float>(v3); uint r = as_type<uint>(t + u); if (exec) { v0 = r; } }
        { float t = as_type<float>(s3) * as_type<float>(v1); float u = as_type<float>(v0); uint r = as_type<uint>(t + u); if (exec) { v7 = r; } }
        { float t = as_type<float>(s0) * as_type<float>(v4); float u = as_type<float>(v5); uint r = as_type<uint>(t + u); if (exec) { v6 = r; } }
        { float t = as_type<float>(s1) * as_type<float>(v7); float u = as_type<float>(v2); uint r = as_type<uint>(t + u); if (exec) { v5 = r; } }
        { float t = as_type<float>(s2) * as_type<float>(v2); float u = as_type<float>(v7); uint r = as_type<uint>(t + u); if (exec) { v4 = r; } }
        { float t = as_type<float>(s3) * as_type<float>(v5); float u = as_type<float>(v4); uint r = as_type<uint>(t + u); if (exec) { v3 = r; } }
        { float t = as_type<float>(s0) * as_type<float>(v0); float u = as_type<float>(v1); uint r = as_type<uint>(t + u); if (exec) { v2 = r; } }
        { float t = as_type<float>(s1) * as_type<float>(v3); float u = as_type<float>(v6); uint r = as_type<uint>(t + u); if (exec) { v1 = r; } }
        { float t = as_type<float>(s2) * as_type<float>(v6); float u = as_type<float>(v3); uint r = as_type<uint>(t + u); if (exec) { v0 = r; } }
        { float t = as_type<float>(s3) * as_type<float>(v1); float u = as_type<float>(v0); uint r = as_type<uint>(t + u); if (exec) { v7 = r; } }
        { float t = as_type<float>(s0) * as_type<float>(v4); float u = as_type<float>(v5); uint r = as_type<uint>(t + u); if (exec) { v6 = r; } }
        { float t = as_type<float>(s1) * as_type<float>(v7); float u = as_type<float>(v2); uint r = as_type<uint>(t + u); if (exec) { v5 = r; } }
        { float t = as_type<float>(s2) * as_type<float>(v2); float u = as_type<float>(v7); uint r = as_type<uint>(t + u); if (exec) { v4 = r; } }
        { float t = as_type<float>(s3) * as_type<float>(v5); float u = as_type<float>(v4); uint r = as_type<uint>(t + u); if (exec) { v3 = r; } }
        { float t = as_type<float>(s0) * as_type<float>(v0); float u = as_type<float>(v1); uint r = as_type<uint>(t + u); if (exec) { v2 = r; } }
        { float t = as_type<float>(s1) * as_type<float>(v3); float u = as_type<float>(v6); uint r = as_type<uint>(t + u); if (exec) { v1 = r; } }
        { float t = as_type<float>(s2) * as_type<float>(v6); float u = as_type<float>(v3); uint r = as_type<uint>(t + u); if (exec) { v0 = r; } }
        { float t = as_type<float>(s3) * as_type<float>(v1); float u = as_type<float>(v0); uint r = as_type<uint>(t + u); if (exec) { v7 = r; } }
        { float t = as_type<float>(s0) * as_type<float>(v4); float u = as_type<float>(v5); uint r = as_type<uint>(t + u); if (exec) { v6 = r; } }
        { float t = as_type<float>(s1) * as_type<float>(v7); float u = as_type<float>(v2); uint r = as_type<uint>(t + u); if (exec) { v5 = r; } }
        { float t = as_type<float>(s2) * as_type<float>(v2); float u = as_type<float>(v7); uint r = as_type<uint>(t + u); if (exec) { v4 = r; } }
        { float t = as_type<float>(s3) * as_type<float>(v5); float u = as_type<float>(v4); uint r = as_type<uint>(t + u); if (exec) { v3 = r; } }
    }
    out[tid] = v0 ^ v1 ^ v2 ^ v3 ^ v4 ^ v5 ^ v6 ^ v7;
}
""",
        ["exec_unguarded"] = """
// bench group=exec_guard variant=unguarded threads=1048576 tg=32 iters=64 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    uint m = data[0];
    bool exec = ((m >> lane) & 1u) != 0u;
    uint v0 = (data[(tid * 3u) & 0x3FFFFFu] >> 9) | 0x3F800000u, v1 = (data[(tid * 5u + 1u) & 0x3FFFFFu] >> 9) | 0x3F800000u;
    uint v2 = (data[(tid * 7u + 2u) & 0x3FFFFFu] >> 9) | 0x3F800000u, v3 = (data[(tid * 11u + 3u) & 0x3FFFFFu] >> 9) | 0x3F800000u;
    uint v4 = v0 ^ 0x1234u, v5 = v1 ^ 0x4321u, v6 = v2 ^ 0x777u, v7 = v3 ^ 0x999u;
    uint s0 = 0x3F000000u | (data[1] & 0xFFFFu), s1 = 0x3F000000u | (data[2] & 0xFFFFu), s2 = 0x3E800000u | (data[3] & 0xFFFFu), s3 = 0x3F7F0000u;
    for (uint it = 0u; it < prm.x; it++)
    {
        { float t = as_type<float>(s0) * as_type<float>(v0); float u = as_type<float>(v1); uint r = as_type<uint>(t + u); v2 = r; }
        { float t = as_type<float>(s1) * as_type<float>(v3); float u = as_type<float>(v6); uint r = as_type<uint>(t + u); v1 = r; }
        { float t = as_type<float>(s2) * as_type<float>(v6); float u = as_type<float>(v3); uint r = as_type<uint>(t + u); v0 = r; }
        { float t = as_type<float>(s3) * as_type<float>(v1); float u = as_type<float>(v0); uint r = as_type<uint>(t + u); v7 = r; }
        { float t = as_type<float>(s0) * as_type<float>(v4); float u = as_type<float>(v5); uint r = as_type<uint>(t + u); v6 = r; }
        { float t = as_type<float>(s1) * as_type<float>(v7); float u = as_type<float>(v2); uint r = as_type<uint>(t + u); v5 = r; }
        { float t = as_type<float>(s2) * as_type<float>(v2); float u = as_type<float>(v7); uint r = as_type<uint>(t + u); v4 = r; }
        { float t = as_type<float>(s3) * as_type<float>(v5); float u = as_type<float>(v4); uint r = as_type<uint>(t + u); v3 = r; }
        { float t = as_type<float>(s0) * as_type<float>(v0); float u = as_type<float>(v1); uint r = as_type<uint>(t + u); v2 = r; }
        { float t = as_type<float>(s1) * as_type<float>(v3); float u = as_type<float>(v6); uint r = as_type<uint>(t + u); v1 = r; }
        { float t = as_type<float>(s2) * as_type<float>(v6); float u = as_type<float>(v3); uint r = as_type<uint>(t + u); v0 = r; }
        { float t = as_type<float>(s3) * as_type<float>(v1); float u = as_type<float>(v0); uint r = as_type<uint>(t + u); v7 = r; }
        { float t = as_type<float>(s0) * as_type<float>(v4); float u = as_type<float>(v5); uint r = as_type<uint>(t + u); v6 = r; }
        { float t = as_type<float>(s1) * as_type<float>(v7); float u = as_type<float>(v2); uint r = as_type<uint>(t + u); v5 = r; }
        { float t = as_type<float>(s2) * as_type<float>(v2); float u = as_type<float>(v7); uint r = as_type<uint>(t + u); v4 = r; }
        { float t = as_type<float>(s3) * as_type<float>(v5); float u = as_type<float>(v4); uint r = as_type<uint>(t + u); v3 = r; }
        { float t = as_type<float>(s0) * as_type<float>(v0); float u = as_type<float>(v1); uint r = as_type<uint>(t + u); v2 = r; }
        { float t = as_type<float>(s1) * as_type<float>(v3); float u = as_type<float>(v6); uint r = as_type<uint>(t + u); v1 = r; }
        { float t = as_type<float>(s2) * as_type<float>(v6); float u = as_type<float>(v3); uint r = as_type<uint>(t + u); v0 = r; }
        { float t = as_type<float>(s3) * as_type<float>(v1); float u = as_type<float>(v0); uint r = as_type<uint>(t + u); v7 = r; }
        { float t = as_type<float>(s0) * as_type<float>(v4); float u = as_type<float>(v5); uint r = as_type<uint>(t + u); v6 = r; }
        { float t = as_type<float>(s1) * as_type<float>(v7); float u = as_type<float>(v2); uint r = as_type<uint>(t + u); v5 = r; }
        { float t = as_type<float>(s2) * as_type<float>(v2); float u = as_type<float>(v7); uint r = as_type<uint>(t + u); v4 = r; }
        { float t = as_type<float>(s3) * as_type<float>(v5); float u = as_type<float>(v4); uint r = as_type<uint>(t + u); v3 = r; }
    }
    out[tid] = v0 ^ v1 ^ v2 ^ v3 ^ v4 ^ v5 ^ v6 ^ v7;
}
""",
        ["f16_current"] = """
// bench group=f16_pack variant=current threads=1048576 tg=64 iters=32 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)

static inline float rtz_cur(float t)
{
    return (isnan(t) || isinf(t)) ? t : (fabs(t) < 6.103515625e-05f ? (trunc(t * 16777216.0f) * 5.9604644775390625e-08f) : as_type<float>(as_type<uint>(clamp(t, -65504.0f, 65504.0f)) & 0xFFFFE000u));
}
static inline uint pack_cur(float x, float y)
{
    return ((uint)as_type<ushort>(half(rtz_cur(x)))) | (((uint)as_type<ushort>(half(rtz_cur(y)))) << 16);
}
// Integer round-toward-zero f32 -> f16 bits, saturating finite overflow to 65504.
static inline uint rtz_bits(uint f)
{
    uint sign = (f >> 16) & 0x8000u;
    uint a = f & 0x7FFFFFFFu;
    uint normal = min((a - 0x38000000u) >> 13, 0x7BFFu);
    uint e = a >> 23;
    uint m = (a & 0x7FFFFFu) | 0x800000u;
    uint denorm = e >= 102u ? (m >> (126u - e)) : 0u;
    uint special = a > 0x7F800000u ? (0x7E00u | ((a >> 13) & 0x1FFu)) : 0x7C00u;
    uint r = a >= 0x7F800000u ? special : (a >= 0x38800000u ? normal : denorm);
    return sign | r;
}
static inline uint pack_int(float x, float y)
{
    return rtz_bits(as_type<uint>(x)) | (rtz_bits(as_type<uint>(y)) << 16);
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    uint acc = 0u;
    for (uint i = 0u; i < prm.x; i++)
    {
        uint a = data[(tid * 2u + i * 977u) & 0x3FFFFFu];
        uint b = data[(tid * 2u + i * 977u + 1u) & 0x3FFFFFu];
        // exponents spread over denormal half, normal half and overflow; no NaN or infinity
        uint ea = 90u + (a >> 27); uint eb = 90u + (b >> 27);
        float x = as_type<float>((a & 0x807FFFFFu) | (ea << 23));
        float y = as_type<float>((b & 0x807FFFFFu) | (eb << 23));
        acc ^= pack_cur(x, y);
        acc = acc * 3u + pack_cur(y, x);
    }
    out[tid] = acc;
}
""",
        ["f16_int_rtz"] = """
// bench group=f16_pack variant=int_rtz threads=1048576 tg=64 iters=32 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)

static inline float rtz_cur(float t)
{
    return (isnan(t) || isinf(t)) ? t : (fabs(t) < 6.103515625e-05f ? (trunc(t * 16777216.0f) * 5.9604644775390625e-08f) : as_type<float>(as_type<uint>(clamp(t, -65504.0f, 65504.0f)) & 0xFFFFE000u));
}
static inline uint pack_cur(float x, float y)
{
    return ((uint)as_type<ushort>(half(rtz_cur(x)))) | (((uint)as_type<ushort>(half(rtz_cur(y)))) << 16);
}
// Integer round-toward-zero f32 -> f16 bits, saturating finite overflow to 65504.
static inline uint rtz_bits(uint f)
{
    uint sign = (f >> 16) & 0x8000u;
    uint a = f & 0x7FFFFFFFu;
    uint normal = min((a - 0x38000000u) >> 13, 0x7BFFu);
    uint e = a >> 23;
    uint m = (a & 0x7FFFFFu) | 0x800000u;
    uint denorm = e >= 102u ? (m >> (126u - e)) : 0u;
    uint special = a > 0x7F800000u ? (0x7E00u | ((a >> 13) & 0x1FFu)) : 0x7C00u;
    uint r = a >= 0x7F800000u ? special : (a >= 0x38800000u ? normal : denorm);
    return sign | r;
}
static inline uint pack_int(float x, float y)
{
    return rtz_bits(as_type<uint>(x)) | (rtz_bits(as_type<uint>(y)) << 16);
}

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    uint acc = 0u;
    for (uint i = 0u; i < prm.x; i++)
    {
        uint a = data[(tid * 2u + i * 977u) & 0x3FFFFFu];
        uint b = data[(tid * 2u + i * 977u + 1u) & 0x3FFFFFu];
        // exponents spread over denormal half, normal half and overflow; no NaN or infinity
        uint ea = 90u + (a >> 27); uint eb = 90u + (b >> 27);
        float x = as_type<float>((a & 0x807FFFFFu) | (ea << 23));
        float y = as_type<float>((b & 0x807FFFFFu) | (eb << 23));
        acc ^= pack_int(x, y);
        acc = acc * 3u + pack_int(y, x);
    }
    out[tid] = acc;
}
""",
        ["lds_clampmask_noguard"] = """
// bench group=lds_index variant=clampmask_noguard threads=262144 tg=32 iters=32 check=1
#include <metal_stdlib>
using namespace metal;
kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]], uint tgi [[thread_index_in_threadgroup]])
{
    threadgroup uint lds[1281];
    uint m = data[0];
    bool exec = ((m >> lane) & 1u) != 0u;
    uint acc = tid;
    uint base = (tgi & 31u) * 4u;
    for (uint it = 0u; it < prm.x; it++)
    {
        uint a = base + (it & 7u) * 128u;
    lds[min(((((a) + 1360u) >> 2) & 2047u), 1279u)] = acc + 0u;
    lds[min(((((a) + 1364u) >> 2) & 2047u), 1279u)] = acc + 1u;
    lds[min(((((a) + 1368u) >> 2) & 2047u), 1279u)] = acc + 2u;
    lds[min(((((a) + 1372u) >> 2) & 2047u), 1279u)] = acc + 3u;
    lds[min(((((a) + 1376u) >> 2) & 2047u), 1279u)] = acc + 4u;
    lds[min(((((a) + 1380u) >> 2) & 2047u), 1279u)] = acc + 5u;
    lds[min(((((a) + 1384u) >> 2) & 2047u), 1279u)] = acc + 6u;
    lds[min(((((a) + 1388u) >> 2) & 2047u), 1279u)] = acc + 7u;
    lds[min(((((a) + 1392u) >> 2) & 2047u), 1279u)] = acc + 8u;
    lds[min(((((a) + 1396u) >> 2) & 2047u), 1279u)] = acc + 9u;
    lds[min(((((a) + 1400u) >> 2) & 2047u), 1279u)] = acc + 10u;
    lds[min(((((a) + 1404u) >> 2) & 2047u), 1279u)] = acc + 11u;
    lds[min(((((a) + 1408u) >> 2) & 2047u), 1279u)] = acc + 12u;
    lds[min(((((a) + 1412u) >> 2) & 2047u), 1279u)] = acc + 13u;
    lds[min(((((a) + 1416u) >> 2) & 2047u), 1279u)] = acc + 14u;
    lds[min(((((a) + 1420u) >> 2) & 2047u), 1279u)] = acc + 15u;
        threadgroup_barrier(mem_flags::mem_threadgroup);
    acc ^= lds[min(((((a) + 1360u) >> 2) & 2047u), 1279u)];
    acc ^= lds[min(((((a) + 1364u) >> 2) & 2047u), 1279u)];
    acc ^= lds[min(((((a) + 1368u) >> 2) & 2047u), 1279u)];
    acc ^= lds[min(((((a) + 1372u) >> 2) & 2047u), 1279u)];
    acc ^= lds[min(((((a) + 1376u) >> 2) & 2047u), 1279u)];
    acc ^= lds[min(((((a) + 1380u) >> 2) & 2047u), 1279u)];
    acc ^= lds[min(((((a) + 1384u) >> 2) & 2047u), 1279u)];
    acc ^= lds[min(((((a) + 1388u) >> 2) & 2047u), 1279u)];
    acc ^= lds[min(((((a) + 1392u) >> 2) & 2047u), 1279u)];
    acc ^= lds[min(((((a) + 1396u) >> 2) & 2047u), 1279u)];
    acc ^= lds[min(((((a) + 1400u) >> 2) & 2047u), 1279u)];
    acc ^= lds[min(((((a) + 1404u) >> 2) & 2047u), 1279u)];
    acc ^= lds[min(((((a) + 1408u) >> 2) & 2047u), 1279u)];
    acc ^= lds[min(((((a) + 1412u) >> 2) & 2047u), 1279u)];
    acc ^= lds[min(((((a) + 1416u) >> 2) & 2047u), 1279u)];
    acc ^= lds[min(((((a) + 1420u) >> 2) & 2047u), 1279u)];
        threadgroup_barrier(mem_flags::mem_threadgroup);
    }
    out[tid] = acc;
}
""",
        ["lds_current"] = """
// bench group=lds_index variant=current threads=262144 tg=32 iters=32 check=1
#include <metal_stdlib>
using namespace metal;
kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]], uint tgi [[thread_index_in_threadgroup]])
{
    threadgroup uint lds[1281];
    uint m = data[0];
    bool exec = ((m >> lane) & 1u) != 0u;
    uint acc = tid;
    uint base = (tgi & 31u) * 4u;
    for (uint it = 0u; it < prm.x; it++)
    {
        uint a = base + (it & 7u) * 128u;
    if (exec) { lds[min(((((a) + 1360u) >> 2) & 2047u), 1279u)] = acc + 0u; }
    if (exec) { lds[min(((((a) + 1364u) >> 2) & 2047u), 1279u)] = acc + 1u; }
    if (exec) { lds[min(((((a) + 1368u) >> 2) & 2047u), 1279u)] = acc + 2u; }
    if (exec) { lds[min(((((a) + 1372u) >> 2) & 2047u), 1279u)] = acc + 3u; }
    if (exec) { lds[min(((((a) + 1376u) >> 2) & 2047u), 1279u)] = acc + 4u; }
    if (exec) { lds[min(((((a) + 1380u) >> 2) & 2047u), 1279u)] = acc + 5u; }
    if (exec) { lds[min(((((a) + 1384u) >> 2) & 2047u), 1279u)] = acc + 6u; }
    if (exec) { lds[min(((((a) + 1388u) >> 2) & 2047u), 1279u)] = acc + 7u; }
    if (exec) { lds[min(((((a) + 1392u) >> 2) & 2047u), 1279u)] = acc + 8u; }
    if (exec) { lds[min(((((a) + 1396u) >> 2) & 2047u), 1279u)] = acc + 9u; }
    if (exec) { lds[min(((((a) + 1400u) >> 2) & 2047u), 1279u)] = acc + 10u; }
    if (exec) { lds[min(((((a) + 1404u) >> 2) & 2047u), 1279u)] = acc + 11u; }
    if (exec) { lds[min(((((a) + 1408u) >> 2) & 2047u), 1279u)] = acc + 12u; }
    if (exec) { lds[min(((((a) + 1412u) >> 2) & 2047u), 1279u)] = acc + 13u; }
    if (exec) { lds[min(((((a) + 1416u) >> 2) & 2047u), 1279u)] = acc + 14u; }
    if (exec) { lds[min(((((a) + 1420u) >> 2) & 2047u), 1279u)] = acc + 15u; }
        threadgroup_barrier(mem_flags::mem_threadgroup);
    if (exec) { acc ^= lds[min(((((a) + 1360u) >> 2) & 2047u), 1279u)]; }
    if (exec) { acc ^= lds[min(((((a) + 1364u) >> 2) & 2047u), 1279u)]; }
    if (exec) { acc ^= lds[min(((((a) + 1368u) >> 2) & 2047u), 1279u)]; }
    if (exec) { acc ^= lds[min(((((a) + 1372u) >> 2) & 2047u), 1279u)]; }
    if (exec) { acc ^= lds[min(((((a) + 1376u) >> 2) & 2047u), 1279u)]; }
    if (exec) { acc ^= lds[min(((((a) + 1380u) >> 2) & 2047u), 1279u)]; }
    if (exec) { acc ^= lds[min(((((a) + 1384u) >> 2) & 2047u), 1279u)]; }
    if (exec) { acc ^= lds[min(((((a) + 1388u) >> 2) & 2047u), 1279u)]; }
    if (exec) { acc ^= lds[min(((((a) + 1392u) >> 2) & 2047u), 1279u)]; }
    if (exec) { acc ^= lds[min(((((a) + 1396u) >> 2) & 2047u), 1279u)]; }
    if (exec) { acc ^= lds[min(((((a) + 1400u) >> 2) & 2047u), 1279u)]; }
    if (exec) { acc ^= lds[min(((((a) + 1404u) >> 2) & 2047u), 1279u)]; }
    if (exec) { acc ^= lds[min(((((a) + 1408u) >> 2) & 2047u), 1279u)]; }
    if (exec) { acc ^= lds[min(((((a) + 1412u) >> 2) & 2047u), 1279u)]; }
    if (exec) { acc ^= lds[min(((((a) + 1416u) >> 2) & 2047u), 1279u)]; }
    if (exec) { acc ^= lds[min(((((a) + 1420u) >> 2) & 2047u), 1279u)]; }
        threadgroup_barrier(mem_flags::mem_threadgroup);
    }
    out[tid] = acc;
}
""",
        ["lds_guard_only"] = """
// bench group=lds_index variant=guard_only threads=262144 tg=32 iters=32 check=1
#include <metal_stdlib>
using namespace metal;
kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]], uint tgi [[thread_index_in_threadgroup]])
{
    threadgroup uint lds[1281];
    uint m = data[0];
    bool exec = ((m >> lane) & 1u) != 0u;
    uint acc = tid;
    uint base = (tgi & 31u) * 4u;
    for (uint it = 0u; it < prm.x; it++)
    {
        uint a = base + (it & 7u) * 128u;
    if (exec) { lds[(((a) + 1360u) >> 2)] = acc + 0u; }
    if (exec) { lds[(((a) + 1364u) >> 2)] = acc + 1u; }
    if (exec) { lds[(((a) + 1368u) >> 2)] = acc + 2u; }
    if (exec) { lds[(((a) + 1372u) >> 2)] = acc + 3u; }
    if (exec) { lds[(((a) + 1376u) >> 2)] = acc + 4u; }
    if (exec) { lds[(((a) + 1380u) >> 2)] = acc + 5u; }
    if (exec) { lds[(((a) + 1384u) >> 2)] = acc + 6u; }
    if (exec) { lds[(((a) + 1388u) >> 2)] = acc + 7u; }
    if (exec) { lds[(((a) + 1392u) >> 2)] = acc + 8u; }
    if (exec) { lds[(((a) + 1396u) >> 2)] = acc + 9u; }
    if (exec) { lds[(((a) + 1400u) >> 2)] = acc + 10u; }
    if (exec) { lds[(((a) + 1404u) >> 2)] = acc + 11u; }
    if (exec) { lds[(((a) + 1408u) >> 2)] = acc + 12u; }
    if (exec) { lds[(((a) + 1412u) >> 2)] = acc + 13u; }
    if (exec) { lds[(((a) + 1416u) >> 2)] = acc + 14u; }
    if (exec) { lds[(((a) + 1420u) >> 2)] = acc + 15u; }
        threadgroup_barrier(mem_flags::mem_threadgroup);
    if (exec) { acc ^= lds[(((a) + 1360u) >> 2)]; }
    if (exec) { acc ^= lds[(((a) + 1364u) >> 2)]; }
    if (exec) { acc ^= lds[(((a) + 1368u) >> 2)]; }
    if (exec) { acc ^= lds[(((a) + 1372u) >> 2)]; }
    if (exec) { acc ^= lds[(((a) + 1376u) >> 2)]; }
    if (exec) { acc ^= lds[(((a) + 1380u) >> 2)]; }
    if (exec) { acc ^= lds[(((a) + 1384u) >> 2)]; }
    if (exec) { acc ^= lds[(((a) + 1388u) >> 2)]; }
    if (exec) { acc ^= lds[(((a) + 1392u) >> 2)]; }
    if (exec) { acc ^= lds[(((a) + 1396u) >> 2)]; }
    if (exec) { acc ^= lds[(((a) + 1400u) >> 2)]; }
    if (exec) { acc ^= lds[(((a) + 1404u) >> 2)]; }
    if (exec) { acc ^= lds[(((a) + 1408u) >> 2)]; }
    if (exec) { acc ^= lds[(((a) + 1412u) >> 2)]; }
    if (exec) { acc ^= lds[(((a) + 1416u) >> 2)]; }
    if (exec) { acc ^= lds[(((a) + 1420u) >> 2)]; }
        threadgroup_barrier(mem_flags::mem_threadgroup);
    }
    out[tid] = acc;
}
""",
        ["lds_loadsel"] = """
// bench group=lds_index variant=loadsel threads=262144 tg=32 iters=32 check=1
#include <metal_stdlib>
using namespace metal;
kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]], uint tgi [[thread_index_in_threadgroup]])
{
    threadgroup uint lds[1281];
    uint m = data[0];
    bool exec = ((m >> lane) & 1u) != 0u;
    uint acc = tid;
    uint base = (tgi & 31u) * 4u;
    for (uint it = 0u; it < prm.x; it++)
    {
        uint a = base + (it & 7u) * 128u;
    if (exec) { lds[min(((((a) + 1360u) >> 2) & 2047u), 1279u)] = acc + 0u; }
    if (exec) { lds[min(((((a) + 1364u) >> 2) & 2047u), 1279u)] = acc + 1u; }
    if (exec) { lds[min(((((a) + 1368u) >> 2) & 2047u), 1279u)] = acc + 2u; }
    if (exec) { lds[min(((((a) + 1372u) >> 2) & 2047u), 1279u)] = acc + 3u; }
    if (exec) { lds[min(((((a) + 1376u) >> 2) & 2047u), 1279u)] = acc + 4u; }
    if (exec) { lds[min(((((a) + 1380u) >> 2) & 2047u), 1279u)] = acc + 5u; }
    if (exec) { lds[min(((((a) + 1384u) >> 2) & 2047u), 1279u)] = acc + 6u; }
    if (exec) { lds[min(((((a) + 1388u) >> 2) & 2047u), 1279u)] = acc + 7u; }
    if (exec) { lds[min(((((a) + 1392u) >> 2) & 2047u), 1279u)] = acc + 8u; }
    if (exec) { lds[min(((((a) + 1396u) >> 2) & 2047u), 1279u)] = acc + 9u; }
    if (exec) { lds[min(((((a) + 1400u) >> 2) & 2047u), 1279u)] = acc + 10u; }
    if (exec) { lds[min(((((a) + 1404u) >> 2) & 2047u), 1279u)] = acc + 11u; }
    if (exec) { lds[min(((((a) + 1408u) >> 2) & 2047u), 1279u)] = acc + 12u; }
    if (exec) { lds[min(((((a) + 1412u) >> 2) & 2047u), 1279u)] = acc + 13u; }
    if (exec) { lds[min(((((a) + 1416u) >> 2) & 2047u), 1279u)] = acc + 14u; }
    if (exec) { lds[min(((((a) + 1420u) >> 2) & 2047u), 1279u)] = acc + 15u; }
        threadgroup_barrier(mem_flags::mem_threadgroup);
    { uint r = lds[min(((((a) + 1360u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1364u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1368u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1372u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1376u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1380u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1384u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1388u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1392u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1396u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1400u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1404u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1408u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1412u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1416u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1420u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
        threadgroup_barrier(mem_flags::mem_threadgroup);
    }
    out[tid] = acc;
}
""",
        ["lds_loadsel_storeslot"] = """
// bench group=lds_index variant=loadsel_storeslot threads=262144 tg=32 iters=32 check=1
#include <metal_stdlib>
using namespace metal;
kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]], uint tgi [[thread_index_in_threadgroup]])
{
    threadgroup uint lds[1281];
    uint m = data[0];
    bool exec = ((m >> lane) & 1u) != 0u;
    uint acc = tid;
    uint base = (tgi & 31u) * 4u;
    for (uint it = 0u; it < prm.x; it++)
    {
        uint a = base + (it & 7u) * 128u;
    lds[exec ? min(((((a) + 1360u) >> 2) & 2047u), 1279u) : 1280u] = acc + 0u;
    lds[exec ? min(((((a) + 1364u) >> 2) & 2047u), 1279u) : 1280u] = acc + 1u;
    lds[exec ? min(((((a) + 1368u) >> 2) & 2047u), 1279u) : 1280u] = acc + 2u;
    lds[exec ? min(((((a) + 1372u) >> 2) & 2047u), 1279u) : 1280u] = acc + 3u;
    lds[exec ? min(((((a) + 1376u) >> 2) & 2047u), 1279u) : 1280u] = acc + 4u;
    lds[exec ? min(((((a) + 1380u) >> 2) & 2047u), 1279u) : 1280u] = acc + 5u;
    lds[exec ? min(((((a) + 1384u) >> 2) & 2047u), 1279u) : 1280u] = acc + 6u;
    lds[exec ? min(((((a) + 1388u) >> 2) & 2047u), 1279u) : 1280u] = acc + 7u;
    lds[exec ? min(((((a) + 1392u) >> 2) & 2047u), 1279u) : 1280u] = acc + 8u;
    lds[exec ? min(((((a) + 1396u) >> 2) & 2047u), 1279u) : 1280u] = acc + 9u;
    lds[exec ? min(((((a) + 1400u) >> 2) & 2047u), 1279u) : 1280u] = acc + 10u;
    lds[exec ? min(((((a) + 1404u) >> 2) & 2047u), 1279u) : 1280u] = acc + 11u;
    lds[exec ? min(((((a) + 1408u) >> 2) & 2047u), 1279u) : 1280u] = acc + 12u;
    lds[exec ? min(((((a) + 1412u) >> 2) & 2047u), 1279u) : 1280u] = acc + 13u;
    lds[exec ? min(((((a) + 1416u) >> 2) & 2047u), 1279u) : 1280u] = acc + 14u;
    lds[exec ? min(((((a) + 1420u) >> 2) & 2047u), 1279u) : 1280u] = acc + 15u;
        threadgroup_barrier(mem_flags::mem_threadgroup);
    { uint r = lds[min(((((a) + 1360u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1364u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1368u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1372u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1376u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1380u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1384u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1388u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1392u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1396u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1400u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1404u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1408u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1412u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1416u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
    { uint r = lds[min(((((a) + 1420u) >> 2) & 2047u), 1279u)]; if (exec) { acc ^= r; } }
        threadgroup_barrier(mem_flags::mem_threadgroup);
    }
    out[tid] = acc;
}
""",
        ["lds_mask_only"] = """
// bench group=lds_index variant=mask_only threads=262144 tg=32 iters=32 check=1
#include <metal_stdlib>
using namespace metal;
kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]], uint tgi [[thread_index_in_threadgroup]])
{
    threadgroup uint lds[1281];
    uint m = data[0];
    bool exec = ((m >> lane) & 1u) != 0u;
    uint acc = tid;
    uint base = (tgi & 31u) * 4u;
    for (uint it = 0u; it < prm.x; it++)
    {
        uint a = base + (it & 7u) * 128u;
    lds[((((a) + 1360u) >> 2) & 2047u)] = acc + 0u;
    lds[((((a) + 1364u) >> 2) & 2047u)] = acc + 1u;
    lds[((((a) + 1368u) >> 2) & 2047u)] = acc + 2u;
    lds[((((a) + 1372u) >> 2) & 2047u)] = acc + 3u;
    lds[((((a) + 1376u) >> 2) & 2047u)] = acc + 4u;
    lds[((((a) + 1380u) >> 2) & 2047u)] = acc + 5u;
    lds[((((a) + 1384u) >> 2) & 2047u)] = acc + 6u;
    lds[((((a) + 1388u) >> 2) & 2047u)] = acc + 7u;
    lds[((((a) + 1392u) >> 2) & 2047u)] = acc + 8u;
    lds[((((a) + 1396u) >> 2) & 2047u)] = acc + 9u;
    lds[((((a) + 1400u) >> 2) & 2047u)] = acc + 10u;
    lds[((((a) + 1404u) >> 2) & 2047u)] = acc + 11u;
    lds[((((a) + 1408u) >> 2) & 2047u)] = acc + 12u;
    lds[((((a) + 1412u) >> 2) & 2047u)] = acc + 13u;
    lds[((((a) + 1416u) >> 2) & 2047u)] = acc + 14u;
    lds[((((a) + 1420u) >> 2) & 2047u)] = acc + 15u;
        threadgroup_barrier(mem_flags::mem_threadgroup);
    acc ^= lds[((((a) + 1360u) >> 2) & 2047u)];
    acc ^= lds[((((a) + 1364u) >> 2) & 2047u)];
    acc ^= lds[((((a) + 1368u) >> 2) & 2047u)];
    acc ^= lds[((((a) + 1372u) >> 2) & 2047u)];
    acc ^= lds[((((a) + 1376u) >> 2) & 2047u)];
    acc ^= lds[((((a) + 1380u) >> 2) & 2047u)];
    acc ^= lds[((((a) + 1384u) >> 2) & 2047u)];
    acc ^= lds[((((a) + 1388u) >> 2) & 2047u)];
    acc ^= lds[((((a) + 1392u) >> 2) & 2047u)];
    acc ^= lds[((((a) + 1396u) >> 2) & 2047u)];
    acc ^= lds[((((a) + 1400u) >> 2) & 2047u)];
    acc ^= lds[((((a) + 1404u) >> 2) & 2047u)];
    acc ^= lds[((((a) + 1408u) >> 2) & 2047u)];
    acc ^= lds[((((a) + 1412u) >> 2) & 2047u)];
    acc ^= lds[((((a) + 1416u) >> 2) & 2047u)];
    acc ^= lds[((((a) + 1420u) >> 2) & 2047u)];
        threadgroup_barrier(mem_flags::mem_threadgroup);
    }
    out[tid] = acc;
}
""",
        ["lds_noclamp_guard"] = """
// bench group=lds_index variant=noclamp_guard threads=262144 tg=32 iters=32 check=1
#include <metal_stdlib>
using namespace metal;
kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]], uint tgi [[thread_index_in_threadgroup]])
{
    threadgroup uint lds[1281];
    uint m = data[0];
    bool exec = ((m >> lane) & 1u) != 0u;
    uint acc = tid;
    uint base = (tgi & 31u) * 4u;
    for (uint it = 0u; it < prm.x; it++)
    {
        uint a = base + (it & 7u) * 128u;
    if (exec) { lds[((((a) + 1360u) >> 2) & 2047u)] = acc + 0u; }
    if (exec) { lds[((((a) + 1364u) >> 2) & 2047u)] = acc + 1u; }
    if (exec) { lds[((((a) + 1368u) >> 2) & 2047u)] = acc + 2u; }
    if (exec) { lds[((((a) + 1372u) >> 2) & 2047u)] = acc + 3u; }
    if (exec) { lds[((((a) + 1376u) >> 2) & 2047u)] = acc + 4u; }
    if (exec) { lds[((((a) + 1380u) >> 2) & 2047u)] = acc + 5u; }
    if (exec) { lds[((((a) + 1384u) >> 2) & 2047u)] = acc + 6u; }
    if (exec) { lds[((((a) + 1388u) >> 2) & 2047u)] = acc + 7u; }
    if (exec) { lds[((((a) + 1392u) >> 2) & 2047u)] = acc + 8u; }
    if (exec) { lds[((((a) + 1396u) >> 2) & 2047u)] = acc + 9u; }
    if (exec) { lds[((((a) + 1400u) >> 2) & 2047u)] = acc + 10u; }
    if (exec) { lds[((((a) + 1404u) >> 2) & 2047u)] = acc + 11u; }
    if (exec) { lds[((((a) + 1408u) >> 2) & 2047u)] = acc + 12u; }
    if (exec) { lds[((((a) + 1412u) >> 2) & 2047u)] = acc + 13u; }
    if (exec) { lds[((((a) + 1416u) >> 2) & 2047u)] = acc + 14u; }
    if (exec) { lds[((((a) + 1420u) >> 2) & 2047u)] = acc + 15u; }
        threadgroup_barrier(mem_flags::mem_threadgroup);
    if (exec) { acc ^= lds[((((a) + 1360u) >> 2) & 2047u)]; }
    if (exec) { acc ^= lds[((((a) + 1364u) >> 2) & 2047u)]; }
    if (exec) { acc ^= lds[((((a) + 1368u) >> 2) & 2047u)]; }
    if (exec) { acc ^= lds[((((a) + 1372u) >> 2) & 2047u)]; }
    if (exec) { acc ^= lds[((((a) + 1376u) >> 2) & 2047u)]; }
    if (exec) { acc ^= lds[((((a) + 1380u) >> 2) & 2047u)]; }
    if (exec) { acc ^= lds[((((a) + 1384u) >> 2) & 2047u)]; }
    if (exec) { acc ^= lds[((((a) + 1388u) >> 2) & 2047u)]; }
    if (exec) { acc ^= lds[((((a) + 1392u) >> 2) & 2047u)]; }
    if (exec) { acc ^= lds[((((a) + 1396u) >> 2) & 2047u)]; }
    if (exec) { acc ^= lds[((((a) + 1400u) >> 2) & 2047u)]; }
    if (exec) { acc ^= lds[((((a) + 1404u) >> 2) & 2047u)]; }
    if (exec) { acc ^= lds[((((a) + 1408u) >> 2) & 2047u)]; }
    if (exec) { acc ^= lds[((((a) + 1412u) >> 2) & 2047u)]; }
    if (exec) { acc ^= lds[((((a) + 1416u) >> 2) & 2047u)]; }
    if (exec) { acc ^= lds[((((a) + 1420u) >> 2) & 2047u)]; }
        threadgroup_barrier(mem_flags::mem_threadgroup);
    }
    out[tid] = acc;
}
""",
        ["lds_plain"] = """
// bench group=lds_index variant=plain threads=262144 tg=32 iters=32 check=1
#include <metal_stdlib>
using namespace metal;
kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]], uint tgi [[thread_index_in_threadgroup]])
{
    threadgroup uint lds[1281];
    uint m = data[0];
    bool exec = ((m >> lane) & 1u) != 0u;
    uint acc = tid;
    uint base = (tgi & 31u) * 4u;
    for (uint it = 0u; it < prm.x; it++)
    {
        uint a = base + (it & 7u) * 128u;
    lds[(((a) + 1360u) >> 2)] = acc + 0u;
    lds[(((a) + 1364u) >> 2)] = acc + 1u;
    lds[(((a) + 1368u) >> 2)] = acc + 2u;
    lds[(((a) + 1372u) >> 2)] = acc + 3u;
    lds[(((a) + 1376u) >> 2)] = acc + 4u;
    lds[(((a) + 1380u) >> 2)] = acc + 5u;
    lds[(((a) + 1384u) >> 2)] = acc + 6u;
    lds[(((a) + 1388u) >> 2)] = acc + 7u;
    lds[(((a) + 1392u) >> 2)] = acc + 8u;
    lds[(((a) + 1396u) >> 2)] = acc + 9u;
    lds[(((a) + 1400u) >> 2)] = acc + 10u;
    lds[(((a) + 1404u) >> 2)] = acc + 11u;
    lds[(((a) + 1408u) >> 2)] = acc + 12u;
    lds[(((a) + 1412u) >> 2)] = acc + 13u;
    lds[(((a) + 1416u) >> 2)] = acc + 14u;
    lds[(((a) + 1420u) >> 2)] = acc + 15u;
        threadgroup_barrier(mem_flags::mem_threadgroup);
    acc ^= lds[(((a) + 1360u) >> 2)];
    acc ^= lds[(((a) + 1364u) >> 2)];
    acc ^= lds[(((a) + 1368u) >> 2)];
    acc ^= lds[(((a) + 1372u) >> 2)];
    acc ^= lds[(((a) + 1376u) >> 2)];
    acc ^= lds[(((a) + 1380u) >> 2)];
    acc ^= lds[(((a) + 1384u) >> 2)];
    acc ^= lds[(((a) + 1388u) >> 2)];
    acc ^= lds[(((a) + 1392u) >> 2)];
    acc ^= lds[(((a) + 1396u) >> 2)];
    acc ^= lds[(((a) + 1400u) >> 2)];
    acc ^= lds[(((a) + 1404u) >> 2)];
    acc ^= lds[(((a) + 1408u) >> 2)];
    acc ^= lds[(((a) + 1412u) >> 2)];
    acc ^= lds[(((a) + 1416u) >> 2)];
    acc ^= lds[(((a) + 1420u) >> 2)];
        threadgroup_barrier(mem_flags::mem_threadgroup);
    }
    out[tid] = acc;
}
""",
        ["mad_contract_on"] = """
// bench group=contract variant=pragma_on_relaxed threads=1048576 tg=64 iters=64
#include <metal_stdlib>
using namespace metal;

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    uint m = data[0];
    bool exec = ((m >> lane) & 1u) != 0u;
    uint v0 = (data[(tid * 3u) & 0x3FFFFFu] >> 9) | 0x3F800000u, v1 = (data[(tid * 5u + 1u) & 0x3FFFFFu] >> 9) | 0x3F800000u;
    uint v2 = (data[(tid * 7u + 2u) & 0x3FFFFFu] >> 9) | 0x3F800000u, v3 = (data[(tid * 11u + 3u) & 0x3FFFFFu] >> 9) | 0x3F800000u;
    uint v4 = v0 ^ 0x1234u, v5 = v1 ^ 0x4321u, v6 = v2 ^ 0x777u, v7 = v3 ^ 0x999u;
    uint s0 = 0x3F000000u | (data[1] & 0xFFFFu), s1 = 0x3F000000u | (data[2] & 0xFFFFu), s2 = 0x3E800000u | (data[3] & 0xFFFFu), s3 = 0x3F7F0000u;
    for (uint it = 0u; it < prm.x; it++)
    {
        { float t = as_type<float>(s0) * as_type<float>(v0); float u = as_type<float>(v1); uint r = as_type<uint>(t + u); v2 = r; }
        { float t = as_type<float>(s1) * as_type<float>(v3); float u = as_type<float>(v6); uint r = as_type<uint>(t + u); v1 = r; }
        { float t = as_type<float>(s2) * as_type<float>(v6); float u = as_type<float>(v3); uint r = as_type<uint>(t + u); v0 = r; }
        { float t = as_type<float>(s3) * as_type<float>(v1); float u = as_type<float>(v0); uint r = as_type<uint>(t + u); v7 = r; }
        { float t = as_type<float>(s0) * as_type<float>(v4); float u = as_type<float>(v5); uint r = as_type<uint>(t + u); v6 = r; }
        { float t = as_type<float>(s1) * as_type<float>(v7); float u = as_type<float>(v2); uint r = as_type<uint>(t + u); v5 = r; }
        { float t = as_type<float>(s2) * as_type<float>(v2); float u = as_type<float>(v7); uint r = as_type<uint>(t + u); v4 = r; }
        { float t = as_type<float>(s3) * as_type<float>(v5); float u = as_type<float>(v4); uint r = as_type<uint>(t + u); v3 = r; }
        { float t = as_type<float>(s0) * as_type<float>(v0); float u = as_type<float>(v1); uint r = as_type<uint>(t + u); v2 = r; }
        { float t = as_type<float>(s1) * as_type<float>(v3); float u = as_type<float>(v6); uint r = as_type<uint>(t + u); v1 = r; }
        { float t = as_type<float>(s2) * as_type<float>(v6); float u = as_type<float>(v3); uint r = as_type<uint>(t + u); v0 = r; }
        { float t = as_type<float>(s3) * as_type<float>(v1); float u = as_type<float>(v0); uint r = as_type<uint>(t + u); v7 = r; }
        { float t = as_type<float>(s0) * as_type<float>(v4); float u = as_type<float>(v5); uint r = as_type<uint>(t + u); v6 = r; }
        { float t = as_type<float>(s1) * as_type<float>(v7); float u = as_type<float>(v2); uint r = as_type<uint>(t + u); v5 = r; }
        { float t = as_type<float>(s2) * as_type<float>(v2); float u = as_type<float>(v7); uint r = as_type<uint>(t + u); v4 = r; }
        { float t = as_type<float>(s3) * as_type<float>(v5); float u = as_type<float>(v4); uint r = as_type<uint>(t + u); v3 = r; }
        { float t = as_type<float>(s0) * as_type<float>(v0); float u = as_type<float>(v1); uint r = as_type<uint>(t + u); v2 = r; }
        { float t = as_type<float>(s1) * as_type<float>(v3); float u = as_type<float>(v6); uint r = as_type<uint>(t + u); v1 = r; }
        { float t = as_type<float>(s2) * as_type<float>(v6); float u = as_type<float>(v3); uint r = as_type<uint>(t + u); v0 = r; }
        { float t = as_type<float>(s3) * as_type<float>(v1); float u = as_type<float>(v0); uint r = as_type<uint>(t + u); v7 = r; }
        { float t = as_type<float>(s0) * as_type<float>(v4); float u = as_type<float>(v5); uint r = as_type<uint>(t + u); v6 = r; }
        { float t = as_type<float>(s1) * as_type<float>(v7); float u = as_type<float>(v2); uint r = as_type<uint>(t + u); v5 = r; }
        { float t = as_type<float>(s2) * as_type<float>(v2); float u = as_type<float>(v7); uint r = as_type<uint>(t + u); v4 = r; }
        { float t = as_type<float>(s3) * as_type<float>(v5); float u = as_type<float>(v4); uint r = as_type<uint>(t + u); v3 = r; }
    }
    out[tid] = v0 ^ v1 ^ v2 ^ v3 ^ v4 ^ v5 ^ v6 ^ v7;
}
""",
        ["mad_fma"] = """
// bench group=contract variant=fma threads=1048576 tg=64 iters=64
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    uint m = data[0];
    bool exec = ((m >> lane) & 1u) != 0u;
    uint v0 = (data[(tid * 3u) & 0x3FFFFFu] >> 9) | 0x3F800000u, v1 = (data[(tid * 5u + 1u) & 0x3FFFFFu] >> 9) | 0x3F800000u;
    uint v2 = (data[(tid * 7u + 2u) & 0x3FFFFFu] >> 9) | 0x3F800000u, v3 = (data[(tid * 11u + 3u) & 0x3FFFFFu] >> 9) | 0x3F800000u;
    uint v4 = v0 ^ 0x1234u, v5 = v1 ^ 0x4321u, v6 = v2 ^ 0x777u, v7 = v3 ^ 0x999u;
    uint s0 = 0x3F000000u | (data[1] & 0xFFFFu), s1 = 0x3F000000u | (data[2] & 0xFFFFu), s2 = 0x3E800000u | (data[3] & 0xFFFFu), s3 = 0x3F7F0000u;
    for (uint it = 0u; it < prm.x; it++)
    {
        { uint r = as_type<uint>(fma(as_type<float>(s0), as_type<float>(v0), as_type<float>(v1))); v2 = r; }
        { uint r = as_type<uint>(fma(as_type<float>(s1), as_type<float>(v3), as_type<float>(v6))); v1 = r; }
        { uint r = as_type<uint>(fma(as_type<float>(s2), as_type<float>(v6), as_type<float>(v3))); v0 = r; }
        { uint r = as_type<uint>(fma(as_type<float>(s3), as_type<float>(v1), as_type<float>(v0))); v7 = r; }
        { uint r = as_type<uint>(fma(as_type<float>(s0), as_type<float>(v4), as_type<float>(v5))); v6 = r; }
        { uint r = as_type<uint>(fma(as_type<float>(s1), as_type<float>(v7), as_type<float>(v2))); v5 = r; }
        { uint r = as_type<uint>(fma(as_type<float>(s2), as_type<float>(v2), as_type<float>(v7))); v4 = r; }
        { uint r = as_type<uint>(fma(as_type<float>(s3), as_type<float>(v5), as_type<float>(v4))); v3 = r; }
        { uint r = as_type<uint>(fma(as_type<float>(s0), as_type<float>(v0), as_type<float>(v1))); v2 = r; }
        { uint r = as_type<uint>(fma(as_type<float>(s1), as_type<float>(v3), as_type<float>(v6))); v1 = r; }
        { uint r = as_type<uint>(fma(as_type<float>(s2), as_type<float>(v6), as_type<float>(v3))); v0 = r; }
        { uint r = as_type<uint>(fma(as_type<float>(s3), as_type<float>(v1), as_type<float>(v0))); v7 = r; }
        { uint r = as_type<uint>(fma(as_type<float>(s0), as_type<float>(v4), as_type<float>(v5))); v6 = r; }
        { uint r = as_type<uint>(fma(as_type<float>(s1), as_type<float>(v7), as_type<float>(v2))); v5 = r; }
        { uint r = as_type<uint>(fma(as_type<float>(s2), as_type<float>(v2), as_type<float>(v7))); v4 = r; }
        { uint r = as_type<uint>(fma(as_type<float>(s3), as_type<float>(v5), as_type<float>(v4))); v3 = r; }
        { uint r = as_type<uint>(fma(as_type<float>(s0), as_type<float>(v0), as_type<float>(v1))); v2 = r; }
        { uint r = as_type<uint>(fma(as_type<float>(s1), as_type<float>(v3), as_type<float>(v6))); v1 = r; }
        { uint r = as_type<uint>(fma(as_type<float>(s2), as_type<float>(v6), as_type<float>(v3))); v0 = r; }
        { uint r = as_type<uint>(fma(as_type<float>(s3), as_type<float>(v1), as_type<float>(v0))); v7 = r; }
        { uint r = as_type<uint>(fma(as_type<float>(s0), as_type<float>(v4), as_type<float>(v5))); v6 = r; }
        { uint r = as_type<uint>(fma(as_type<float>(s1), as_type<float>(v7), as_type<float>(v2))); v5 = r; }
        { uint r = as_type<uint>(fma(as_type<float>(s2), as_type<float>(v2), as_type<float>(v7))); v4 = r; }
        { uint r = as_type<uint>(fma(as_type<float>(s3), as_type<float>(v5), as_type<float>(v4))); v3 = r; }
    }
    out[tid] = v0 ^ v1 ^ v2 ^ v3 ^ v4 ^ v5 ^ v6 ^ v7;
}
""",
        ["mad_separate"] = """
// bench group=contract variant=current_mul_add threads=1048576 tg=64 iters=64
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)

kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    uint m = data[0];
    bool exec = ((m >> lane) & 1u) != 0u;
    uint v0 = (data[(tid * 3u) & 0x3FFFFFu] >> 9) | 0x3F800000u, v1 = (data[(tid * 5u + 1u) & 0x3FFFFFu] >> 9) | 0x3F800000u;
    uint v2 = (data[(tid * 7u + 2u) & 0x3FFFFFu] >> 9) | 0x3F800000u, v3 = (data[(tid * 11u + 3u) & 0x3FFFFFu] >> 9) | 0x3F800000u;
    uint v4 = v0 ^ 0x1234u, v5 = v1 ^ 0x4321u, v6 = v2 ^ 0x777u, v7 = v3 ^ 0x999u;
    uint s0 = 0x3F000000u | (data[1] & 0xFFFFu), s1 = 0x3F000000u | (data[2] & 0xFFFFu), s2 = 0x3E800000u | (data[3] & 0xFFFFu), s3 = 0x3F7F0000u;
    for (uint it = 0u; it < prm.x; it++)
    {
        { float t = as_type<float>(s0) * as_type<float>(v0); float u = as_type<float>(v1); uint r = as_type<uint>(t + u); v2 = r; }
        { float t = as_type<float>(s1) * as_type<float>(v3); float u = as_type<float>(v6); uint r = as_type<uint>(t + u); v1 = r; }
        { float t = as_type<float>(s2) * as_type<float>(v6); float u = as_type<float>(v3); uint r = as_type<uint>(t + u); v0 = r; }
        { float t = as_type<float>(s3) * as_type<float>(v1); float u = as_type<float>(v0); uint r = as_type<uint>(t + u); v7 = r; }
        { float t = as_type<float>(s0) * as_type<float>(v4); float u = as_type<float>(v5); uint r = as_type<uint>(t + u); v6 = r; }
        { float t = as_type<float>(s1) * as_type<float>(v7); float u = as_type<float>(v2); uint r = as_type<uint>(t + u); v5 = r; }
        { float t = as_type<float>(s2) * as_type<float>(v2); float u = as_type<float>(v7); uint r = as_type<uint>(t + u); v4 = r; }
        { float t = as_type<float>(s3) * as_type<float>(v5); float u = as_type<float>(v4); uint r = as_type<uint>(t + u); v3 = r; }
        { float t = as_type<float>(s0) * as_type<float>(v0); float u = as_type<float>(v1); uint r = as_type<uint>(t + u); v2 = r; }
        { float t = as_type<float>(s1) * as_type<float>(v3); float u = as_type<float>(v6); uint r = as_type<uint>(t + u); v1 = r; }
        { float t = as_type<float>(s2) * as_type<float>(v6); float u = as_type<float>(v3); uint r = as_type<uint>(t + u); v0 = r; }
        { float t = as_type<float>(s3) * as_type<float>(v1); float u = as_type<float>(v0); uint r = as_type<uint>(t + u); v7 = r; }
        { float t = as_type<float>(s0) * as_type<float>(v4); float u = as_type<float>(v5); uint r = as_type<uint>(t + u); v6 = r; }
        { float t = as_type<float>(s1) * as_type<float>(v7); float u = as_type<float>(v2); uint r = as_type<uint>(t + u); v5 = r; }
        { float t = as_type<float>(s2) * as_type<float>(v2); float u = as_type<float>(v7); uint r = as_type<uint>(t + u); v4 = r; }
        { float t = as_type<float>(s3) * as_type<float>(v5); float u = as_type<float>(v4); uint r = as_type<uint>(t + u); v3 = r; }
        { float t = as_type<float>(s0) * as_type<float>(v0); float u = as_type<float>(v1); uint r = as_type<uint>(t + u); v2 = r; }
        { float t = as_type<float>(s1) * as_type<float>(v3); float u = as_type<float>(v6); uint r = as_type<uint>(t + u); v1 = r; }
        { float t = as_type<float>(s2) * as_type<float>(v6); float u = as_type<float>(v3); uint r = as_type<uint>(t + u); v0 = r; }
        { float t = as_type<float>(s3) * as_type<float>(v1); float u = as_type<float>(v0); uint r = as_type<uint>(t + u); v7 = r; }
        { float t = as_type<float>(s0) * as_type<float>(v4); float u = as_type<float>(v5); uint r = as_type<uint>(t + u); v6 = r; }
        { float t = as_type<float>(s1) * as_type<float>(v7); float u = as_type<float>(v2); uint r = as_type<uint>(t + u); v5 = r; }
        { float t = as_type<float>(s2) * as_type<float>(v2); float u = as_type<float>(v7); uint r = as_type<uint>(t + u); v4 = r; }
        { float t = as_type<float>(s3) * as_type<float>(v5); float u = as_type<float>(v4); uint r = as_type<uint>(t + u); v3 = r; }
    }
    out[tid] = v0 ^ v1 ^ v2 ^ v3 ^ v4 ^ v5 ^ v6 ^ v7;
}
""",
        ["minmax_current"] = """
// bench group=minmax variant=current threads=1048576 tg=64 iters=64
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    uint v0 = (data[(tid * 3u) & 0x3FFFFFu] >> 9) | 0x3F800000u, v1 = (data[(tid * 5u + 1u) & 0x3FFFFFu] >> 9) | 0x3F000000u;
    uint v2 = (data[(tid * 7u + 2u) & 0x3FFFFFu] >> 9) | 0x3E800000u, v3 = (data[(tid * 11u + 3u) & 0x3FFFFFu] >> 9) | 0x3F800000u;
    for (uint it = 0u; it < prm.x; it++)
    {
        { uint ta = ((v0 & 0x7F800000u) == 0u ? (v0 & 0x80000000u) : v0); uint tb = ((v1 & 0x7F800000u) == 0u ? (v1 & 0x80000000u) : v1); v2 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta | tb) : as_type<uint>(fmin(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = ((v1 & 0x7F800000u) == 0u ? (v1 & 0x80000000u) : v1); uint tb = ((v2 & 0x7F800000u) == 0u ? (v2 & 0x80000000u) : v2); v3 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta & tb) : as_type<uint>(fmax(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = ((v2 & 0x7F800000u) == 0u ? (v2 & 0x80000000u) : v2); uint tb = ((v3 & 0x7F800000u) == 0u ? (v3 & 0x80000000u) : v3); v0 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta | tb) : as_type<uint>(fmin(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = ((v3 & 0x7F800000u) == 0u ? (v3 & 0x80000000u) : v3); uint tb = ((v0 & 0x7F800000u) == 0u ? (v0 & 0x80000000u) : v0); v1 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta & tb) : as_type<uint>(fmax(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = ((v0 & 0x7F800000u) == 0u ? (v0 & 0x80000000u) : v0); uint tb = ((v1 & 0x7F800000u) == 0u ? (v1 & 0x80000000u) : v1); v2 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta | tb) : as_type<uint>(fmin(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = ((v1 & 0x7F800000u) == 0u ? (v1 & 0x80000000u) : v1); uint tb = ((v2 & 0x7F800000u) == 0u ? (v2 & 0x80000000u) : v2); v3 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta & tb) : as_type<uint>(fmax(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = ((v2 & 0x7F800000u) == 0u ? (v2 & 0x80000000u) : v2); uint tb = ((v3 & 0x7F800000u) == 0u ? (v3 & 0x80000000u) : v3); v0 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta | tb) : as_type<uint>(fmin(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = ((v3 & 0x7F800000u) == 0u ? (v3 & 0x80000000u) : v3); uint tb = ((v0 & 0x7F800000u) == 0u ? (v0 & 0x80000000u) : v0); v1 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta & tb) : as_type<uint>(fmax(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = ((v0 & 0x7F800000u) == 0u ? (v0 & 0x80000000u) : v0); uint tb = ((v1 & 0x7F800000u) == 0u ? (v1 & 0x80000000u) : v1); v2 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta | tb) : as_type<uint>(fmin(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = ((v1 & 0x7F800000u) == 0u ? (v1 & 0x80000000u) : v1); uint tb = ((v2 & 0x7F800000u) == 0u ? (v2 & 0x80000000u) : v2); v3 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta & tb) : as_type<uint>(fmax(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = ((v2 & 0x7F800000u) == 0u ? (v2 & 0x80000000u) : v2); uint tb = ((v3 & 0x7F800000u) == 0u ? (v3 & 0x80000000u) : v3); v0 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta | tb) : as_type<uint>(fmin(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = ((v3 & 0x7F800000u) == 0u ? (v3 & 0x80000000u) : v3); uint tb = ((v0 & 0x7F800000u) == 0u ? (v0 & 0x80000000u) : v0); v1 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta & tb) : as_type<uint>(fmax(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = ((v0 & 0x7F800000u) == 0u ? (v0 & 0x80000000u) : v0); uint tb = ((v1 & 0x7F800000u) == 0u ? (v1 & 0x80000000u) : v1); v2 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta | tb) : as_type<uint>(fmin(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = ((v1 & 0x7F800000u) == 0u ? (v1 & 0x80000000u) : v1); uint tb = ((v2 & 0x7F800000u) == 0u ? (v2 & 0x80000000u) : v2); v3 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta & tb) : as_type<uint>(fmax(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = ((v2 & 0x7F800000u) == 0u ? (v2 & 0x80000000u) : v2); uint tb = ((v3 & 0x7F800000u) == 0u ? (v3 & 0x80000000u) : v3); v0 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta | tb) : as_type<uint>(fmin(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = ((v3 & 0x7F800000u) == 0u ? (v3 & 0x80000000u) : v3); uint tb = ((v0 & 0x7F800000u) == 0u ? (v0 & 0x80000000u) : v0); v1 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta & tb) : as_type<uint>(fmax(as_type<float>(ta), as_type<float>(tb)))); }
        v0 += 0x10u;
    }
    out[tid] = v0 ^ v1 ^ v2 ^ v3;
}
""",
        ["minmax_fminfmax"] = """
// bench group=minmax variant=fminfmax threads=1048576 tg=64 iters=64
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    uint v0 = (data[(tid * 3u) & 0x3FFFFFu] >> 9) | 0x3F800000u, v1 = (data[(tid * 5u + 1u) & 0x3FFFFFu] >> 9) | 0x3F000000u;
    uint v2 = (data[(tid * 7u + 2u) & 0x3FFFFFu] >> 9) | 0x3E800000u, v3 = (data[(tid * 11u + 3u) & 0x3FFFFFu] >> 9) | 0x3F800000u;
    for (uint it = 0u; it < prm.x; it++)
    {
        { v2 = as_type<uint>(fmin(as_type<float>(v0), as_type<float>(v1))); }
        { v3 = as_type<uint>(fmax(as_type<float>(v1), as_type<float>(v2))); }
        { v0 = as_type<uint>(fmin(as_type<float>(v2), as_type<float>(v3))); }
        { v1 = as_type<uint>(fmax(as_type<float>(v3), as_type<float>(v0))); }
        { v2 = as_type<uint>(fmin(as_type<float>(v0), as_type<float>(v1))); }
        { v3 = as_type<uint>(fmax(as_type<float>(v1), as_type<float>(v2))); }
        { v0 = as_type<uint>(fmin(as_type<float>(v2), as_type<float>(v3))); }
        { v1 = as_type<uint>(fmax(as_type<float>(v3), as_type<float>(v0))); }
        { v2 = as_type<uint>(fmin(as_type<float>(v0), as_type<float>(v1))); }
        { v3 = as_type<uint>(fmax(as_type<float>(v1), as_type<float>(v2))); }
        { v0 = as_type<uint>(fmin(as_type<float>(v2), as_type<float>(v3))); }
        { v1 = as_type<uint>(fmax(as_type<float>(v3), as_type<float>(v0))); }
        { v2 = as_type<uint>(fmin(as_type<float>(v0), as_type<float>(v1))); }
        { v3 = as_type<uint>(fmax(as_type<float>(v1), as_type<float>(v2))); }
        { v0 = as_type<uint>(fmin(as_type<float>(v2), as_type<float>(v3))); }
        { v1 = as_type<uint>(fmax(as_type<float>(v3), as_type<float>(v0))); }
        v0 += 0x10u;
    }
    out[tid] = v0 ^ v1 ^ v2 ^ v3;
}
""",
        ["minmax_noflush"] = """
// bench group=minmax variant=noflush threads=1048576 tg=64 iters=64
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    uint tid [[thread_position_in_grid]], uint lane [[thread_index_in_simdgroup]])
{
    uint v0 = (data[(tid * 3u) & 0x3FFFFFu] >> 9) | 0x3F800000u, v1 = (data[(tid * 5u + 1u) & 0x3FFFFFu] >> 9) | 0x3F000000u;
    uint v2 = (data[(tid * 7u + 2u) & 0x3FFFFFu] >> 9) | 0x3E800000u, v3 = (data[(tid * 11u + 3u) & 0x3FFFFFu] >> 9) | 0x3F800000u;
    for (uint it = 0u; it < prm.x; it++)
    {
        { uint ta = v0; uint tb = v1; v2 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta | tb) : as_type<uint>(fmin(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = v1; uint tb = v2; v3 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta & tb) : as_type<uint>(fmax(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = v2; uint tb = v3; v0 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta | tb) : as_type<uint>(fmin(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = v3; uint tb = v0; v1 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta & tb) : as_type<uint>(fmax(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = v0; uint tb = v1; v2 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta | tb) : as_type<uint>(fmin(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = v1; uint tb = v2; v3 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta & tb) : as_type<uint>(fmax(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = v2; uint tb = v3; v0 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta | tb) : as_type<uint>(fmin(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = v3; uint tb = v0; v1 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta & tb) : as_type<uint>(fmax(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = v0; uint tb = v1; v2 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta | tb) : as_type<uint>(fmin(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = v1; uint tb = v2; v3 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta & tb) : as_type<uint>(fmax(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = v2; uint tb = v3; v0 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta | tb) : as_type<uint>(fmin(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = v3; uint tb = v0; v1 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta & tb) : as_type<uint>(fmax(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = v0; uint tb = v1; v2 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta | tb) : as_type<uint>(fmin(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = v1; uint tb = v2; v3 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta & tb) : as_type<uint>(fmax(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = v2; uint tb = v3; v0 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta | tb) : as_type<uint>(fmin(as_type<float>(ta), as_type<float>(tb)))); }
        { uint ta = v3; uint tb = v0; v1 = isnan(as_type<float>(ta)) ? tb : (as_type<float>(ta) == as_type<float>(tb) ? (ta & tb) : as_type<uint>(fmax(as_type<float>(ta), as_type<float>(tb)))); }
        v0 += 0x10u;
    }
    out[tid] = v0 ^ v1 ^ v2 ^ v3;
}
""",
        ["sdata_current"] = """
// bench group=buf_sdata_prologue variant=current threads=1048576 tg=64 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* sd [[buffer(5)]],
    uint tid [[thread_position_in_grid]])
{
    uint words = prm.y;
    uint s0 = (0u < words ? sd[0u] : 0u);
    uint s1 = (1u < words ? sd[1u] : 0u);
    uint s2 = (2u < words ? sd[2u] : 0u);
    uint s3 = (3u < words ? sd[3u] : 0u);
    uint s4 = (4u < words ? sd[4u] : 0u);
    uint s5 = (5u < words ? sd[5u] : 0u);
    uint s6 = (6u < words ? sd[6u] : 0u);
    uint s7 = (7u < words ? sd[7u] : 0u);
    uint s8 = (8u < words ? sd[8u] : 0u);
    uint s9 = (9u < words ? sd[9u] : 0u);
    uint s10 = (10u < words ? sd[10u] : 0u);
    uint s11 = (11u < words ? sd[11u] : 0u);
    uint s12 = (12u < words ? sd[12u] : 0u);
    uint s13 = (13u < words ? sd[13u] : 0u);
    uint s14 = (14u < words ? sd[14u] : 0u);
    uint s15 = (15u < words ? sd[15u] : 0u);
    uint s16 = (16u < words ? sd[16u] : 0u);
    uint s17 = (17u < words ? sd[17u] : 0u);
    uint s18 = (18u < words ? sd[18u] : 0u);
    uint s19 = (19u < words ? sd[19u] : 0u);
    uint s20 = (20u < words ? sd[20u] : 0u);
    uint s21 = (21u < words ? sd[21u] : 0u);
    uint s22 = (22u < words ? sd[22u] : 0u);
    uint s23 = (23u < words ? sd[23u] : 0u);
    uint s24 = (24u < words ? sd[24u] : 0u);
    uint s25 = (25u < words ? sd[25u] : 0u);
    uint s26 = (26u < words ? sd[26u] : 0u);
    uint s27 = (27u < words ? sd[27u] : 0u);
    uint s28 = (28u < words ? sd[28u] : 0u);
    uint s29 = (29u < words ? sd[29u] : 0u);
    uint s30 = (30u < words ? sd[30u] : 0u);
    uint s31 = (31u < words ? sd[31u] : 0u);
    uint v0 = (data[(tid * 3u) & 0x3FFFFFu] >> 9) | 0x3F800000u, v1 = (data[(tid * 5u + 1u) & 0x3FFFFFu] >> 9) | 0x3F000000u;
    uint v2 = (data[(tid * 7u + 2u) & 0x3FFFFFu] >> 9) | 0x3E800000u, v3 = (data[(tid * 11u + 3u) & 0x3FFFFFu] >> 9) | 0x3F800000u;
    { float t = as_type<float>(s0) * as_type<float>(v0); v1 = as_type<uint>(t + as_type<float>(s3)); }
    { float t = as_type<float>(s1) * as_type<float>(v1); v2 = as_type<uint>(t + as_type<float>(s8)); }
    { float t = as_type<float>(s2) * as_type<float>(v2); v3 = as_type<uint>(t + as_type<float>(s13)); }
    { float t = as_type<float>(s3) * as_type<float>(v3); v0 = as_type<uint>(t + as_type<float>(s18)); }
    { float t = as_type<float>(s4) * as_type<float>(v0); v1 = as_type<uint>(t + as_type<float>(s23)); }
    { float t = as_type<float>(s5) * as_type<float>(v1); v2 = as_type<uint>(t + as_type<float>(s28)); }
    { float t = as_type<float>(s6) * as_type<float>(v2); v3 = as_type<uint>(t + as_type<float>(s1)); }
    { float t = as_type<float>(s7) * as_type<float>(v3); v0 = as_type<uint>(t + as_type<float>(s6)); }
    { float t = as_type<float>(s8) * as_type<float>(v0); v1 = as_type<uint>(t + as_type<float>(s11)); }
    { float t = as_type<float>(s9) * as_type<float>(v1); v2 = as_type<uint>(t + as_type<float>(s16)); }
    { float t = as_type<float>(s10) * as_type<float>(v2); v3 = as_type<uint>(t + as_type<float>(s21)); }
    { float t = as_type<float>(s11) * as_type<float>(v3); v0 = as_type<uint>(t + as_type<float>(s26)); }
    { float t = as_type<float>(s12) * as_type<float>(v0); v1 = as_type<uint>(t + as_type<float>(s31)); }
    { float t = as_type<float>(s13) * as_type<float>(v1); v2 = as_type<uint>(t + as_type<float>(s4)); }
    { float t = as_type<float>(s14) * as_type<float>(v2); v3 = as_type<uint>(t + as_type<float>(s9)); }
    { float t = as_type<float>(s15) * as_type<float>(v3); v0 = as_type<uint>(t + as_type<float>(s14)); }
    { float t = as_type<float>(s16) * as_type<float>(v0); v1 = as_type<uint>(t + as_type<float>(s19)); }
    { float t = as_type<float>(s17) * as_type<float>(v1); v2 = as_type<uint>(t + as_type<float>(s24)); }
    { float t = as_type<float>(s18) * as_type<float>(v2); v3 = as_type<uint>(t + as_type<float>(s29)); }
    { float t = as_type<float>(s19) * as_type<float>(v3); v0 = as_type<uint>(t + as_type<float>(s2)); }
    { float t = as_type<float>(s20) * as_type<float>(v0); v1 = as_type<uint>(t + as_type<float>(s7)); }
    { float t = as_type<float>(s21) * as_type<float>(v1); v2 = as_type<uint>(t + as_type<float>(s12)); }
    { float t = as_type<float>(s22) * as_type<float>(v2); v3 = as_type<uint>(t + as_type<float>(s17)); }
    { float t = as_type<float>(s23) * as_type<float>(v3); v0 = as_type<uint>(t + as_type<float>(s22)); }
    { float t = as_type<float>(s24) * as_type<float>(v0); v1 = as_type<uint>(t + as_type<float>(s27)); }
    { float t = as_type<float>(s25) * as_type<float>(v1); v2 = as_type<uint>(t + as_type<float>(s0)); }
    { float t = as_type<float>(s26) * as_type<float>(v2); v3 = as_type<uint>(t + as_type<float>(s5)); }
    { float t = as_type<float>(s27) * as_type<float>(v3); v0 = as_type<uint>(t + as_type<float>(s10)); }
    { float t = as_type<float>(s28) * as_type<float>(v0); v1 = as_type<uint>(t + as_type<float>(s15)); }
    { float t = as_type<float>(s29) * as_type<float>(v1); v2 = as_type<uint>(t + as_type<float>(s20)); }
    { float t = as_type<float>(s30) * as_type<float>(v2); v3 = as_type<uint>(t + as_type<float>(s25)); }
    { float t = as_type<float>(s31) * as_type<float>(v3); v0 = as_type<uint>(t + as_type<float>(s30)); }
    { float t = as_type<float>(s0) * as_type<float>(v0); v1 = as_type<uint>(t + as_type<float>(s3)); }
    { float t = as_type<float>(s1) * as_type<float>(v1); v2 = as_type<uint>(t + as_type<float>(s8)); }
    { float t = as_type<float>(s2) * as_type<float>(v2); v3 = as_type<uint>(t + as_type<float>(s13)); }
    { float t = as_type<float>(s3) * as_type<float>(v3); v0 = as_type<uint>(t + as_type<float>(s18)); }
    { float t = as_type<float>(s4) * as_type<float>(v0); v1 = as_type<uint>(t + as_type<float>(s23)); }
    { float t = as_type<float>(s5) * as_type<float>(v1); v2 = as_type<uint>(t + as_type<float>(s28)); }
    { float t = as_type<float>(s6) * as_type<float>(v2); v3 = as_type<uint>(t + as_type<float>(s1)); }
    { float t = as_type<float>(s7) * as_type<float>(v3); v0 = as_type<uint>(t + as_type<float>(s6)); }
    out[tid] = v0 ^ v1 ^ v2 ^ v3;
}
""",
        ["sdata_unchecked"] = """
// bench group=buf_sdata_prologue variant=unchecked threads=1048576 tg=64 iters=1 check=1
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
kernel void bench(device uint* data [[buffer(0)]], device uint* out [[buffer(1)]], constant uint4& prm [[buffer(2)]],
    constant uint* sd [[buffer(5)]],
    uint tid [[thread_position_in_grid]])
{
    uint words = prm.y;
    uint s0 = sd[0u];
    uint s1 = sd[1u];
    uint s2 = sd[2u];
    uint s3 = sd[3u];
    uint s4 = sd[4u];
    uint s5 = sd[5u];
    uint s6 = sd[6u];
    uint s7 = sd[7u];
    uint s8 = sd[8u];
    uint s9 = sd[9u];
    uint s10 = sd[10u];
    uint s11 = sd[11u];
    uint s12 = sd[12u];
    uint s13 = sd[13u];
    uint s14 = sd[14u];
    uint s15 = sd[15u];
    uint s16 = sd[16u];
    uint s17 = sd[17u];
    uint s18 = sd[18u];
    uint s19 = sd[19u];
    uint s20 = sd[20u];
    uint s21 = sd[21u];
    uint s22 = sd[22u];
    uint s23 = sd[23u];
    uint s24 = sd[24u];
    uint s25 = sd[25u];
    uint s26 = sd[26u];
    uint s27 = sd[27u];
    uint s28 = sd[28u];
    uint s29 = sd[29u];
    uint s30 = sd[30u];
    uint s31 = sd[31u];
    uint v0 = (data[(tid * 3u) & 0x3FFFFFu] >> 9) | 0x3F800000u, v1 = (data[(tid * 5u + 1u) & 0x3FFFFFu] >> 9) | 0x3F000000u;
    uint v2 = (data[(tid * 7u + 2u) & 0x3FFFFFu] >> 9) | 0x3E800000u, v3 = (data[(tid * 11u + 3u) & 0x3FFFFFu] >> 9) | 0x3F800000u;
    { float t = as_type<float>(s0) * as_type<float>(v0); v1 = as_type<uint>(t + as_type<float>(s3)); }
    { float t = as_type<float>(s1) * as_type<float>(v1); v2 = as_type<uint>(t + as_type<float>(s8)); }
    { float t = as_type<float>(s2) * as_type<float>(v2); v3 = as_type<uint>(t + as_type<float>(s13)); }
    { float t = as_type<float>(s3) * as_type<float>(v3); v0 = as_type<uint>(t + as_type<float>(s18)); }
    { float t = as_type<float>(s4) * as_type<float>(v0); v1 = as_type<uint>(t + as_type<float>(s23)); }
    { float t = as_type<float>(s5) * as_type<float>(v1); v2 = as_type<uint>(t + as_type<float>(s28)); }
    { float t = as_type<float>(s6) * as_type<float>(v2); v3 = as_type<uint>(t + as_type<float>(s1)); }
    { float t = as_type<float>(s7) * as_type<float>(v3); v0 = as_type<uint>(t + as_type<float>(s6)); }
    { float t = as_type<float>(s8) * as_type<float>(v0); v1 = as_type<uint>(t + as_type<float>(s11)); }
    { float t = as_type<float>(s9) * as_type<float>(v1); v2 = as_type<uint>(t + as_type<float>(s16)); }
    { float t = as_type<float>(s10) * as_type<float>(v2); v3 = as_type<uint>(t + as_type<float>(s21)); }
    { float t = as_type<float>(s11) * as_type<float>(v3); v0 = as_type<uint>(t + as_type<float>(s26)); }
    { float t = as_type<float>(s12) * as_type<float>(v0); v1 = as_type<uint>(t + as_type<float>(s31)); }
    { float t = as_type<float>(s13) * as_type<float>(v1); v2 = as_type<uint>(t + as_type<float>(s4)); }
    { float t = as_type<float>(s14) * as_type<float>(v2); v3 = as_type<uint>(t + as_type<float>(s9)); }
    { float t = as_type<float>(s15) * as_type<float>(v3); v0 = as_type<uint>(t + as_type<float>(s14)); }
    { float t = as_type<float>(s16) * as_type<float>(v0); v1 = as_type<uint>(t + as_type<float>(s19)); }
    { float t = as_type<float>(s17) * as_type<float>(v1); v2 = as_type<uint>(t + as_type<float>(s24)); }
    { float t = as_type<float>(s18) * as_type<float>(v2); v3 = as_type<uint>(t + as_type<float>(s29)); }
    { float t = as_type<float>(s19) * as_type<float>(v3); v0 = as_type<uint>(t + as_type<float>(s2)); }
    { float t = as_type<float>(s20) * as_type<float>(v0); v1 = as_type<uint>(t + as_type<float>(s7)); }
    { float t = as_type<float>(s21) * as_type<float>(v1); v2 = as_type<uint>(t + as_type<float>(s12)); }
    { float t = as_type<float>(s22) * as_type<float>(v2); v3 = as_type<uint>(t + as_type<float>(s17)); }
    { float t = as_type<float>(s23) * as_type<float>(v3); v0 = as_type<uint>(t + as_type<float>(s22)); }
    { float t = as_type<float>(s24) * as_type<float>(v0); v1 = as_type<uint>(t + as_type<float>(s27)); }
    { float t = as_type<float>(s25) * as_type<float>(v1); v2 = as_type<uint>(t + as_type<float>(s0)); }
    { float t = as_type<float>(s26) * as_type<float>(v2); v3 = as_type<uint>(t + as_type<float>(s5)); }
    { float t = as_type<float>(s27) * as_type<float>(v3); v0 = as_type<uint>(t + as_type<float>(s10)); }
    { float t = as_type<float>(s28) * as_type<float>(v0); v1 = as_type<uint>(t + as_type<float>(s15)); }
    { float t = as_type<float>(s29) * as_type<float>(v1); v2 = as_type<uint>(t + as_type<float>(s20)); }
    { float t = as_type<float>(s30) * as_type<float>(v2); v3 = as_type<uint>(t + as_type<float>(s25)); }
    { float t = as_type<float>(s31) * as_type<float>(v3); v0 = as_type<uint>(t + as_type<float>(s30)); }
    { float t = as_type<float>(s0) * as_type<float>(v0); v1 = as_type<uint>(t + as_type<float>(s3)); }
    { float t = as_type<float>(s1) * as_type<float>(v1); v2 = as_type<uint>(t + as_type<float>(s8)); }
    { float t = as_type<float>(s2) * as_type<float>(v2); v3 = as_type<uint>(t + as_type<float>(s13)); }
    { float t = as_type<float>(s3) * as_type<float>(v3); v0 = as_type<uint>(t + as_type<float>(s18)); }
    { float t = as_type<float>(s4) * as_type<float>(v0); v1 = as_type<uint>(t + as_type<float>(s23)); }
    { float t = as_type<float>(s5) * as_type<float>(v1); v2 = as_type<uint>(t + as_type<float>(s28)); }
    { float t = as_type<float>(s6) * as_type<float>(v2); v3 = as_type<uint>(t + as_type<float>(s1)); }
    { float t = as_type<float>(s7) * as_type<float>(v3); v0 = as_type<uint>(t + as_type<float>(s6)); }
    out[tid] = v0 ^ v1 ^ v2 ^ v3;
}
""",
    };
}
