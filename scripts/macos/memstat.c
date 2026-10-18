// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
//
// memstat: de-duplicated memory accounting for one process.
//
//   sudo -n artifacts/macos/memstat <pid> [--text]
//
// Walks the task's VM map with mach_vm_region_recurse and counts every VM object once. `footprint`
// and phys_footprint charge each mapping of an object separately, so a shared-memory object that is
// mapped by a canonical alias plus thousands of views (the guest's direct memory) is counted many
// times over. memstat groups regions by VM object id and, per object, sums only the part of the
// object not already counted by a mapping with a lower offset. Where two mappings overlap only
// partly it asks mach_vm_page_range_query about the uncovered pages, so the result is exact for
// resident, dirty and compressed/swapped pages.
//
// --zero-scan: for every VM object classified guest_shm, also read its RESIDENT pages (never the
// compressed or swapped-out ones: reading those would decompress / swap them in) and count the
// pages that are entirely zero. Each page of the object is visited once, by the same union-of-
// mappings walk the unique accounting uses (restricted to readable mappings). Residency comes from
// mach_vm_page_range_query, data from mach_vm_read_overwrite in runs of up to 4 MiB. The zero test
// unit is max(target page size, host page size): a 4 KiB-paged (Rosetta) target still has 16 KiB
// physical pages, so zero is judged per 16 KiB host page, aligned on the object offset, and a unit
// counts only if all its target pages are present (edge units that straddle two mappings are
// skipped). Reported as "zero_scan" in the JSON line, with a histogram of the zero fraction per
// 32 MiB granule of object offset (only granules with resident pages).
//
// Output: one JSON object per run on stdout (or a table with --text). Sizes are bytes.
//   unique   pages counted once per VM object     naive   what summing every region gives
//   phys_footprint etc: the kernel's TASK_VM_INFO for the same instant, for comparison.
//
// Needs task_for_pid: run it as root (passwordless `sudo -n` on the build machine) or on a process
// with get-task-allow. Exit codes: 0 ok, 1 pid gone / no task port, 2 usage.
#include <mach/mach.h>
#include <mach/mach_vm.h>
#include <mach/vm_statistics.h>
#include <libproc.h>
#include <sys/proc_info.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/time.h>
#include <unistd.h>

#define PAGE_QUERY_CHUNK (256u * 1024u)
#define ZERO_READ_CHUNK (4ull << 20)
#define ZERO_GRANULE (32ull << 20)

typedef struct {
  uint64_t object;
  uint64_t offset;
  uint64_t size;
  uint64_t address;
  uint64_t resident, swapped, dirty;
  uint32_t tag;
  uint8_t share_mode;
  uint8_t submap_depth;
  uint8_t protection;
} Region;

enum Category {
  CAT_GUEST_SHM, CAT_ANON, CAT_GRAPHICS, CAT_MALLOC, CAT_ROSETTA, CAT_MAPPED_FILE, CAT_STACK, CAT_OTHER_TAGGED,
  CAT_COUNT
};
static const char *kCategoryNames[CAT_COUNT] = {
  "guest_shm", "anon_untagged", "graphics_iokit", "malloc", "rosetta", "mapped_files", "stack_guard", "other_tagged"
};

typedef struct {
  uint64_t regions, objects, vsize;
  uint64_t resident, dirty, swapped;           // unique, in pages
  uint64_t naive_resident, naive_dirty, naive_swapped;
} CategoryTotals;

typedef struct {
  uint64_t object, views, vsize, resident, dirty, swapped;
  int category;
  uint32_t tag;
  uint8_t share_mode, file_backed, submap_depth;
} ObjectSummary;

static double now_ms(void) {
  struct timeval tv;
  gettimeofday(&tv, NULL);
  return tv.tv_sec * 1e3 + tv.tv_usec / 1e3;
}

static int compare_regions(const void *a, const void *b) {
  const Region *x = a, *y = b;
  if (x->object != y->object) return x->object < y->object ? -1 : 1;
  if (x->offset != y->offset) return x->offset < y->offset ? -1 : 1;
  if (x->size != y->size) return x->size > y->size ? -1 : 1;  // biggest first: it covers the rest
  return x->address < y->address ? -1 : x->address > y->address;
}

static bool is_malloc_tag(uint32_t tag) {
  return (tag >= 1 && tag <= 13 && tag != 5 && tag != 10) || tag == VM_MEMORY_TCMALLOC;
}

static bool is_graphics_tag(uint32_t tag) {
  return tag == VM_MEMORY_IOACCELERATOR || tag == VM_MEMORY_IOSURFACE || tag == VM_MEMORY_IOKIT ||
         tag == VM_MEMORY_COREGRAPHICS || tag == VM_MEMORY_CGIMAGE || tag == VM_MEMORY_COREIMAGE || tag == 87 /* IOSURFACE alt */;
}

static int categorise(const Region *r, bool file_backed, bool multi_view) {
  uint32_t t = r->tag;
  if (r->submap_depth > 0) return CAT_MAPPED_FILE;  // dyld shared cache and other nested submaps
  if (is_graphics_tag(t)) return CAT_GRAPHICS;
  if (is_malloc_tag(t)) return CAT_MALLOC;
  if (t >= VM_MEMORY_ROSETTA && t <= VM_MEMORY_ROSETTA_10) return CAT_ROSETTA;
  if (t == VM_MEMORY_STACK || t == VM_MEMORY_GUARD) return CAT_STACK;
  if (t == 0 || t == VM_MEMORY_DYLIB || t == VM_MEMORY_DYLD) {
    if (file_backed || t == VM_MEMORY_DYLIB || t == VM_MEMORY_DYLD) return CAT_MAPPED_FILE;
    return multi_view ? CAT_GUEST_SHM : CAT_ANON;
  }
  return CAT_OTHER_TAGGED;
}

// Counts the pages of [address, address+size) the VM object has resident, dirty and paged out.
static void query_pages(task_t task, uint64_t address, uint64_t size, uint64_t page_size,
                        uint64_t *resident, uint64_t *dirty, uint64_t *swapped, int *buffer) {
  uint64_t pages = size / page_size;
  for (uint64_t done = 0; done < pages;) {
    uint64_t take = pages - done > PAGE_QUERY_CHUNK ? PAGE_QUERY_CHUNK : pages - done;
    mach_vm_size_t count = take;
    if (mach_vm_page_range_query(task, address + done * page_size, take * page_size,
                                 (mach_vm_address_t)buffer, &count) != KERN_SUCCESS) return;
    for (mach_vm_size_t i = 0; i < count; i++) {
      int d = buffer[i];
      if (d & VM_PAGE_QUERY_PAGE_PRESENT) (*resident)++;
      if (d & VM_PAGE_QUERY_PAGE_DIRTY) (*dirty)++;
      if (d & VM_PAGE_QUERY_PAGE_PAGED_OUT) (*swapped)++;
    }
    done += take;
  }
}

// The kernel's own page counts for the region that contains `address`.
static void full_counts(task_t task, uint64_t address, uint64_t *resident, uint64_t *dirty, uint64_t *swapped) {
  mach_vm_address_t where = address;
  mach_vm_size_t size = 0;
  natural_t depth = 8;
  vm_region_submap_info_data_64_t info;
  mach_msg_type_number_t info_count = VM_REGION_SUBMAP_INFO_COUNT_64;
  if (mach_vm_region_recurse(task, &where, &size, &depth, (vm_region_recurse_info_t)&info, &info_count) != KERN_SUCCESS || where != address) return;
  *resident = info.pages_resident;
  *dirty = info.pages_dirtied;
  *swapped = info.pages_swapped_out;
}

typedef struct {
  uint64_t unit;                                     // zero-test unit in bytes
  uint64_t resident, zero, scanned_bytes, read_errors, objects;
  uint64_t granules_gt90, granules_50_90, granules_lt50;
  uint64_t *gran_resident, *gran_zero;               // per 32 MiB granule of the current object
  uint8_t *buffer;                                   // ZERO_READ_CHUNK
  int *dispositions;
} ZeroScan;

static bool is_zero_block(const uint64_t *words, size_t count) {
  uint64_t acc = 0;
  for (size_t i = 0; i < count; i += 8) {
    acc = words[i] | words[i + 1] | words[i + 2] | words[i + 3] | words[i + 4] | words[i + 5] | words[i + 6] | words[i + 7];
    if (acc) return false;
  }
  return true;
}

// Scans object offsets [from, to) of one mapping: `address` is where `from` is mapped.
static void zero_scan_segment(task_t task, ZeroScan *z, uint64_t address, uint64_t from, uint64_t to, uint64_t page_size) {
  uint64_t skip = (z->unit - from % z->unit) % z->unit;
  address += skip; from += skip;
  to -= to % z->unit;
  uint64_t per_unit = z->unit / page_size;
  for (uint64_t chunk = from; chunk < to; chunk += ZERO_READ_CHUNK) {
    uint64_t bytes = to - chunk < ZERO_READ_CHUNK ? to - chunk : ZERO_READ_CHUNK;
    uint64_t units = bytes / z->unit, pages = bytes / page_size;
    mach_vm_size_t count = pages;
    if (mach_vm_page_range_query(task, address + (chunk - from), bytes, (mach_vm_address_t)z->dispositions, &count) != KERN_SUCCESS ||
        count != pages) {
      z->read_errors++;
      continue;
    }
    for (uint64_t u = 0; u < units;) {
      bool present = true;
      for (uint64_t k = 0; k < per_unit; k++) present &= (z->dispositions[u * per_unit + k] & VM_PAGE_QUERY_PAGE_PRESENT) != 0;
      if (!present) { u++; continue; }
      uint64_t run = u + 1;                           // extend over consecutive present units
      for (; run < units; run++) {
        bool ok = true;
        for (uint64_t k = 0; k < per_unit; k++) ok &= (z->dispositions[run * per_unit + k] & VM_PAGE_QUERY_PAGE_PRESENT) != 0;
        if (!ok) break;
      }
      mach_vm_size_t got = 0;
      uint64_t run_bytes = (run - u) * z->unit;
      if (mach_vm_read_overwrite(task, address + (chunk - from) + u * z->unit, run_bytes, (mach_vm_address_t)z->buffer, &got) != KERN_SUCCESS ||
          got != run_bytes) {
        z->read_errors++;                              // reclaimed or re-protected since the query
        u = run;
        continue;
      }
      z->scanned_bytes += run_bytes;
      for (uint64_t j = u; j < run; j++) {
        bool zero = is_zero_block((const uint64_t *)(z->buffer + (j - u) * z->unit), z->unit / 8);
        uint64_t granule = (chunk + j * z->unit) / ZERO_GRANULE;
        z->resident++; z->gran_resident[granule]++;
        if (zero) { z->zero++; z->gran_zero[granule]++; }
      }
      u = run;
    }
  }
}

// Visits every page of one guest_shm object once: readable mappings in offset order, each scanning only
// the part past what the earlier ones reached.
static void zero_scan_object(task_t task, ZeroScan *z, const Region *regions, size_t first, size_t last, uint64_t page_size) {
  uint64_t top = 0;
  for (size_t i = first; i < last; i++)
    if (regions[i].offset + regions[i].size > top) top = regions[i].offset + regions[i].size;
  size_t granules = top / ZERO_GRANULE + 1;
  z->gran_resident = calloc(granules, sizeof(uint64_t));
  z->gran_zero = calloc(granules, sizeof(uint64_t));
  uint64_t reached = 0;
  bool any = false;
  for (size_t i = first; i < last; i++) {
    const Region *r = &regions[i];
    if (!(r->protection & VM_PROT_READ)) continue;
    uint64_t end = r->offset + r->size, from = r->offset;
    if (any && end <= reached) continue;
    if (any && from < reached) from = reached;
    zero_scan_segment(task, z, r->address + (from - r->offset), from, end, page_size);
    reached = end; any = true;
  }
  for (size_t g = 0; g < granules; g++) {
    if (!z->gran_resident[g]) continue;
    double fraction = (double)z->gran_zero[g] / (double)z->gran_resident[g];
    if (fraction > 0.9) z->granules_gt90++;
    else if (fraction >= 0.5) z->granules_50_90++;
    else z->granules_lt50++;
  }
  free(z->gran_resident); free(z->gran_zero);
  z->objects++;
}

static bool is_file_backed(pid_t pid, uint64_t address) {
  // PROC_PIDREGIONPATHINFO names the vnode behind the region; proc_regionfilename answers with a stale
  // path for shared-memory regions of a translated (Rosetta) process, so it is not used.
  struct proc_regionwithpathinfo info;
  memset(&info, 0, sizeof info);
  return proc_pidinfo(pid, PROC_PIDREGIONPATHINFO, address, &info, sizeof info) > 0 && info.prp_vip.vip_path[0] == '/';
}

static const char *share_mode_name(int mode) {
  switch (mode) {
    case SM_COW: return "cow"; case SM_PRIVATE: return "private"; case SM_EMPTY: return "empty";
    case SM_SHARED: return "shared"; case SM_TRUESHARED: return "trueshared"; case SM_PRIVATE_ALIASED: return "private_aliased";
    case SM_SHARED_ALIASED: return "shared_aliased"; case SM_LARGE_PAGE: return "large_page"; default: return "other";
  }
}

int main(int argc, char **argv) {
  bool text = false, want_naive = false, want_zero = false;
  int pid = 0;
  for (int i = 1; i < argc; i++) {
    if (!strcmp(argv[i], "--text")) text = true;
    else if (!strcmp(argv[i], "--naive")) want_naive = true;  // also sum every region as footprint does (slow)
    else if (!strcmp(argv[i], "--zero-scan")) want_zero = true;  // zero pages among resident guest_shm pages
    else if (argv[i][0] != '-' && !pid) pid = atoi(argv[i]);
    else { pid = 0; break; }
  }
  if (pid <= 0) { fprintf(stderr, "usage: memstat <pid> [--text] [--naive] [--zero-scan]   (needs task_for_pid: sudo -n)\n"); return 2; }

  double start = now_ms();
  struct timeval wall;
  gettimeofday(&wall, NULL);
  task_t task = MACH_PORT_NULL;
  kern_return_t kr = task_for_pid(mach_task_self(), pid, &task);
  if (kr != KERN_SUCCESS) {
    fprintf(stderr, "memstat: task_for_pid(%d) failed: %s (run with sudo -n, or the pid is gone)\n", pid, mach_error_string(kr));
    return 1;
  }

  task_vm_info_data_t vm = {0};
  mach_msg_type_number_t vm_count = TASK_VM_INFO_COUNT;
  if (task_info(task, TASK_VM_INFO, (task_info_t)&vm, &vm_count) != KERN_SUCCESS) { fprintf(stderr, "memstat: task_info failed\n"); return 1; }
  uint64_t page_size = vm.page_size > 0 ? (uint64_t)vm.page_size : 16384;

  // Pass 1 uses the short submap info, which names each region's object and offset but does not make
  // the kernel count its pages; counting is the expensive part and is only done for pages not yet
  // covered by another mapping of the same object (pass 2).
  size_t capacity = 1 << 16, count = 0;
  Region *regions = malloc(capacity * sizeof(Region));
  uint64_t address = 0;
  for (;;) {
    mach_vm_size_t size = 0;
    natural_t depth = 8;  // deep enough that every returned region is a leaf
    vm_region_submap_short_info_data_64_t info;
    mach_msg_type_number_t info_count = VM_REGION_SUBMAP_SHORT_INFO_COUNT_64;
    kr = mach_vm_region_recurse(task, (mach_vm_address_t *)&address, &size, &depth, (vm_region_recurse_info_t)&info, &info_count);
    if (kr != KERN_SUCCESS) break;
    if (count == capacity) { capacity *= 2; regions = realloc(regions, capacity * sizeof(Region)); }
    Region *r = &regions[count++];
    r->object = info.object_id;
    r->offset = info.offset;
    r->size = size;
    r->address = address;
    r->resident = r->swapped = r->dirty = 0;
    r->tag = info.user_tag;
    r->share_mode = info.share_mode;
    r->submap_depth = depth;
    r->protection = info.protection;
    address += size;
  }
  double walked = now_ms();
  qsort(regions, count, sizeof(Region), compare_regions);

  CategoryTotals totals[CAT_COUNT] = {{0}};
  uint64_t tag_resident[256] = {0};
  uint64_t object_count = 0, unique_resident = 0, unique_dirty = 0, unique_swapped = 0;
  uint64_t naive_resident = 0, naive_dirty = 0, naive_swapped = 0, total_vsize = 0, partial_queries = 0, file_probes = 0;
  size_t top_capacity = 8, top_count = 0;
  ObjectSummary top[8];
  int *page_buffer = malloc(PAGE_QUERY_CHUNK * sizeof(int));
  ZeroScan zero = {0};
  double zero_ms = 0;
  if (want_zero) {
    uint64_t host_page = (uint64_t)getpagesize();
    zero.unit = page_size > host_page ? page_size : host_page;
    zero.buffer = malloc(ZERO_READ_CHUNK);
    zero.dispositions = malloc((ZERO_READ_CHUNK / page_size) * sizeof(int));
  }

  for (size_t first = 0; first < count;) {
    size_t last = first;
    while (last < count && regions[last].object == regions[first].object) last++;
    size_t views = last - first;
    // Category of the object: its largest region decides; a file probe only for plain untagged objects.
    const Region *lead = &regions[first];
    for (size_t i = first + 1; i < last; i++)
      if (regions[i].size > lead->size) lead = &regions[i];
    bool file_backed = false;
    if (lead->tag == 0 && lead->submap_depth == 0 && lead->share_mode != SM_EMPTY) {
      file_backed = is_file_backed(pid, lead->address);
      file_probes++;
    }
    int category = categorise(lead, file_backed, views > 1);

    uint64_t covered_end = 0, res = 0, dirty = 0, swp = 0, vsize = 0;
    bool any = false;
    for (size_t i = first; i < last; i++) {
      Region *r = &regions[i];
      total_vsize += r->size;
      vsize += r->size;
      totals[category].regions++;
      uint64_t end = r->offset + r->size;
      uint64_t add_res = 0, add_dirty = 0, add_swp = 0;
      bool counted = false;
      if (!any || r->offset >= covered_end) {
        full_counts(task, r->address, &add_res, &add_dirty, &add_swp);
        r->resident = add_res; r->dirty = add_dirty; r->swapped = add_swp; counted = true;
        covered_end = end; any = true;
      } else if (end > covered_end) {
        uint64_t skip = covered_end - r->offset;
        query_pages(task, r->address + skip, r->size - skip, page_size, &add_res, &add_dirty, &add_swp, page_buffer);
        partial_queries++;
        covered_end = end;
      }
      if (want_naive) {
        if (!counted) full_counts(task, r->address, &r->resident, &r->dirty, &r->swapped);
        naive_resident += r->resident; naive_dirty += r->dirty; naive_swapped += r->swapped;
        totals[category].naive_resident += r->resident;
      }
      res += add_res; dirty += add_dirty; swp += add_swp;
      tag_resident[r->tag & 255] += add_res;
    }
    if (want_zero && category == CAT_GUEST_SHM) {
      double began = now_ms();
      zero_scan_object(task, &zero, regions, first, last, page_size);
      zero_ms += now_ms() - began;
    }
    object_count++;
    totals[category].objects++;
    totals[category].vsize += vsize;
    totals[category].resident += res; totals[category].dirty += dirty; totals[category].swapped += swp;
    unique_resident += res; unique_dirty += dirty; unique_swapped += swp;
    // Keep the largest objects by unique resident + swapped pages.
    ObjectSummary summary = {lead->object, views, vsize, res, dirty, swp, category, lead->tag, lead->share_mode, file_backed, lead->submap_depth};
    if (top_count < top_capacity) {
      top[top_count++] = summary;
    } else {
      size_t slot = 0;
      for (size_t i = 1; i < top_capacity; i++) if (top[i].resident + top[i].swapped < top[slot].resident + top[slot].swapped) slot = i;
      if (res + swp > top[slot].resident + top[slot].swapped) top[slot] = summary;
    }
    first = last;
  }
  double done = now_ms();

  for (size_t i = 0; i + 1 < top_count; i++)
    for (size_t j = i + 1; j < top_count; j++)
      if (top[j].resident + top[j].swapped > top[i].resident + top[i].swapped) { ObjectSummary t = top[i]; top[i] = top[j]; top[j] = t; }

  uint64_t B = page_size;
  if (text) {
    printf("pid %d  page %llu  regions %zu  objects %llu  walk %.0f ms  total %.0f ms\n", pid, (unsigned long long)page_size, count,
           (unsigned long long)object_count, walked - start, done - start);
    printf("%-16s %8s %8s %12s %12s %12s %12s %12s\n", "category", "regions", "objects", "vsize_MB", "res_MB", "dirty_MB", "swapped_MB", "naive_res_MB");
    for (int c = 0; c < CAT_COUNT; c++) {
      CategoryTotals *t = &totals[c];
      printf("%-16s %8llu %8llu %12.1f %12.1f %12.1f %12.1f %12.1f\n", kCategoryNames[c], (unsigned long long)t->regions,
             (unsigned long long)t->objects, t->vsize / 1048576.0, t->resident * B / 1048576.0, t->dirty * B / 1048576.0,
             t->swapped * B / 1048576.0, t->naive_resident * B / 1048576.0);  // 0 without --naive
    }
    printf("%-16s %8zu %8llu %12.1f %12.1f %12.1f %12.1f %12.1f\n", "TOTAL", count, (unsigned long long)object_count, total_vsize / 1048576.0,
           unique_resident * B / 1048576.0, unique_dirty * B / 1048576.0, unique_swapped * B / 1048576.0, naive_resident * B / 1048576.0);
    if (want_zero)
      printf("zero_scan: unit %llu  objects %llu  resident %llu pages  zero %llu pages (%.1f MB)  scanned %.1f MB  errors %llu  granules >90%% %llu  50-90%% %llu  <50%% %llu  %.0f ms\n",
             (unsigned long long)zero.unit, (unsigned long long)zero.objects, (unsigned long long)zero.resident, (unsigned long long)zero.zero,
             zero.zero * zero.unit / 1048576.0, zero.scanned_bytes / 1048576.0, (unsigned long long)zero.read_errors,
             (unsigned long long)zero.granules_gt90, (unsigned long long)zero.granules_50_90, (unsigned long long)zero.granules_lt50, zero_ms);
    printf("phys_footprint %.1f MB  internal %.1f  external %.1f  compressed %.1f  reusable %.1f  peak %.1f MB\n",
           vm.phys_footprint / 1048576.0, vm.internal / 1048576.0, vm.external / 1048576.0, vm.compressed / 1048576.0,
           vm.reusable / 1048576.0, vm.ledger_phys_footprint_peak / 1048576.0);
    return 0;
  }

  printf("{\"t\":%ld.%03d,\"pid\":%d,\"page_size\":%llu,\"regions\":%zu,\"objects\":%llu,\"walk_ms\":%.1f,\"total_ms\":%.1f,"
         "\"partial_queries\":%llu,\"file_probes\":%llu,\"vsize\":%llu,",
         (long)wall.tv_sec, (int)(wall.tv_usec / 1000), pid, (unsigned long long)page_size, count, (unsigned long long)object_count,
         walked - start, done - start, (unsigned long long)partial_queries, (unsigned long long)file_probes, (unsigned long long)total_vsize);
  printf("\"unique\":{\"resident\":%llu,\"dirty\":%llu,\"swapped\":%llu},", (unsigned long long)(unique_resident * B),
         (unsigned long long)(unique_dirty * B), (unsigned long long)(unique_swapped * B));
  if (want_naive)
    printf("\"naive\":{\"resident\":%llu,\"dirty\":%llu,\"swapped\":%llu},", (unsigned long long)(naive_resident * B),
           (unsigned long long)(naive_dirty * B), (unsigned long long)(naive_swapped * B));
  if (want_zero)
    printf("\"zero_scan\":{\"page_size\":%llu,\"objects\":%llu,\"resident_pages\":%llu,\"zero_pages\":%llu,\"zero_bytes\":%llu,"
           "\"scanned_bytes\":%llu,\"read_errors\":%llu,\"ms\":%.1f,\"granule_mib\":%llu,"
           "\"granules\":{\"zero_gt90\":%llu,\"zero_50_90\":%llu,\"zero_lt50\":%llu}},",
           (unsigned long long)zero.unit, (unsigned long long)zero.objects, (unsigned long long)zero.resident, (unsigned long long)zero.zero,
           (unsigned long long)(zero.zero * zero.unit), (unsigned long long)zero.scanned_bytes, (unsigned long long)zero.read_errors, zero_ms,
           (unsigned long long)(ZERO_GRANULE >> 20), (unsigned long long)zero.granules_gt90, (unsigned long long)zero.granules_50_90,
           (unsigned long long)zero.granules_lt50);
  printf("\"task\":{\"phys_footprint\":%llu,\"footprint_peak\":%llu,\"internal\":%llu,\"external\":%llu,\"compressed\":%llu,"
         "\"reusable\":%llu,\"resident_size\":%llu,\"virtual_size\":%llu},",
         (unsigned long long)vm.phys_footprint, (unsigned long long)vm.ledger_phys_footprint_peak, (unsigned long long)vm.internal,
         (unsigned long long)vm.external, (unsigned long long)vm.compressed, (unsigned long long)vm.reusable,
         (unsigned long long)vm.resident_size, (unsigned long long)vm.virtual_size);
  printf("\"categories\":{");
  for (int c = 0; c < CAT_COUNT; c++) {
    CategoryTotals *t = &totals[c];
    printf("%s\"%s\":{\"regions\":%llu,\"objects\":%llu,\"vsize\":%llu,\"resident\":%llu,\"dirty\":%llu,\"swapped\":%llu",
           c ? "," : "", kCategoryNames[c], (unsigned long long)t->regions, (unsigned long long)t->objects, (unsigned long long)t->vsize,
           (unsigned long long)(t->resident * B), (unsigned long long)(t->dirty * B), (unsigned long long)(t->swapped * B));
    if (want_naive) printf(",\"naive_resident\":%llu", (unsigned long long)(t->naive_resident * B));
    printf("}");
  }
  printf("},\"tags\":{");
  bool first_tag = true;
  for (int tag = 0; tag < 256; tag++) {
    if (!tag_resident[tag]) continue;
    printf("%s\"%d\":%llu", first_tag ? "" : ",", tag, (unsigned long long)(tag_resident[tag] * B));
    first_tag = false;
  }
  printf("},\"top_objects\":[");
  for (size_t i = 0; i < top_count; i++) {
    printf("%s{\"id\":\"0x%llx\",\"category\":\"%s\",\"views\":%llu,\"vsize\":%llu,\"resident\":%llu,\"dirty\":%llu,\"swapped\":%llu,\"tag\":%u,\"share_mode\":\"%s\",\"file\":%d,\"depth\":%d}", i ? "," : "",
           (unsigned long long)top[i].object, kCategoryNames[top[i].category], (unsigned long long)top[i].views, (unsigned long long)top[i].vsize,
           (unsigned long long)(top[i].resident * B), (unsigned long long)(top[i].dirty * B), (unsigned long long)(top[i].swapped * B), top[i].tag, share_mode_name(top[i].share_mode), top[i].file_backed, top[i].submap_depth);
  }
  printf("]}\n");
  return 0;
}
