// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.DsTrace;

/// <summary>
/// Every DSTRACE record type. The numeric value is only the in-ring tag; the file
/// carries the name. Types other agents emit later are defined here already so
/// that adding one is a single Emit call.
/// </summary>
public enum DsEv : ushort
{
    None = 0,
    STR,
    // scheduler
    GF, BRA, WAKE, PUMPA, USLA, USLP,
    // HLE
    HLE, HLEA, CPUPT,
    // fiber
    FSW, FSJ,
    // sync
    MXQ, MXH, MXR, EVF, EVS, APR,
    // memory locks
    MLW, MLA,
    // video out / AGC
    FLIP, SUBMIT, SUSP, FLIPDONE, PRESENT,
    // Metal pipeline
    QENQ, SLICE, CPS, CPW, WIDLE, MCOMMIT, MGPU, LENC, LLAND, RRW,
    // guest-state sampler
    ROOTS, SRT, SFC, SWK, SLK, CSAT, JD, SCNT, SGRP, SPOOL, SFM, SSTAT,
    // host sampler
    THR, TATTR, CLK, PROC, OTHER, TCPU, WCPU, GC, AB,
    // writer
    WSTAT,
    Count,
}

/// <summary>
/// The positional field list of every record type, in the order the emitter passes
/// P0..P12. This one table generates the SCHEMA lines and drives the formatter.
/// Suffix <c>:x</c> prints hex (0x…); <c>:s</c> marks a STR id (printed decimal);
/// everything else prints as signed 64-bit decimal.
/// </summary>
internal static class DsSchema
{
    private static readonly (DsEv Type, string Fields)[] _table =
    [
        (DsEv.GF, "kind guest:x name:s reason:s wake:s nid:s arg0:x arg1:x deadline"),
        (DsEv.BRA, "guest:x nid:s ra1:x ra2:x ra3:x"),
        (DsEv.WAKE, "enter_ts gate_wait_ns total_ns wake:s scanned woken first_woken:x pumped"),
        (DsEv.PUMPA, "calls cas_lost cap_hit claimed wake_expired_ns"),
        (DsEv.USLA, "ra1:x count req_us_sum actual_ns_sum pump_ns_sum sleep0_count"),
        (DsEv.USLP, "ra1:x ra2:x req_us actual_ns pump_ns branch"),

        (DsEv.HLE, "enter_ts dur_ns cpu_ns import:s rdi:x rsi:x rdx:x ra1:x ra2:x outcome rax:x"),
        (DsEv.HLEA, "import:s count wall_sum_ns wall_max_ns n_ge_100us n_ge_1ms n_ge_10ms cpu_sum_ns cpu_n wall_sum_of_cpu_sampled_ns"),
        (DsEv.CPUPT, "cpu_ns"),

        (DsEv.FSW, "enter_ts total_ns gate_wait_ns body_ns prep_ns probe_code probe_wlock_wait_ns probe_mprotect_ns cpu_ns from:x to:x ra2:x ra1:x"),
        (DsEv.FSJ, "job:x counter:x value target cond_flags aff_grp q08:x q10:x q58:x q60:x ra3:x ra4:x ra5:x"),

        (DsEv.MXQ, "mutex:x waiter:x owner:x qlen spin_ns outcome ra1:x"),
        // MXH: an unlock passed the mutex on to waiter `to`. FIFO handoff (SHARPEMU_MUTEX_HANDOFF=fifo):
        // ownership was granted to `to`. Release-and-wake (default): the mutex was released and `to`,
        // the queue head, was woken to re-contend (it may lose to a barger and stay queued).
        // to_parked=1 when the wake went through the guest scheduler (a parked cooperative waiter);
        // qlen_after counts queued waiters, including a woken head that is still queued.
        (DsEv.MXH, "mutex:x from:x to:x to_parked qlen_after"),
        (DsEv.MXR, "mutex:x guest:x wait_ns"),
        (DsEv.EVF, "handle:x bits:x mode path total_ns loop_iters ra1:x"),
        (DsEv.EVS, "handle:x bits:x woken"),
        (DsEv.APR, "ra1:x ra2:x n_cmds total_ns pread_ns waitaddr_ns waitaddr_iters bytes"),

        (DsEv.MLW, "site wait_ns hold_ns import:s thread_class in_rip_probe"),
        (DsEv.MLA, "site tid count slow_count wait_sum_ns wait_max_ns hold_sum_ns hold_max_ns"),

        (DsEv.FLIP, "seq rdi:x rsi:x rdx:x rcx:x r8:x ra1:x ra2:x"),
        (DsEv.SUBMIT, "rdi:x dwords gate_wait_ns total_ns submission_id ra2:x"),
        (DsEv.SUSP, "wait_ns boundaries_pending"),
        (DsEv.FLIPDONE, "request_id buffer"),
        (DsEv.PRESENT, "request_id"),

        (DsEv.QENQ, "queue submission_id kind dwords"),
        (DsEv.SLICE, "start_ts dur_ns queue submission_id packets outcome wait_addr:x cpu_ns"),
        (DsEv.CPS, "queue kind addr:x ref:x mask:x func cur:x"),
        (DsEv.CPW, "source woken oldest_blocked_ns"),
        (DsEv.WIDLE, "start_ts dur_ns reason outstanding sample_addr:x oldest_age_us cpu_ns"),
        (DsEv.MCOMMIT, "serial cause pending_labels submission_id"),
        (DsEv.MGPU, "serial kstart_ns kend_ns gstart_ns gend_ns late"),
        (DsEv.LENC, "serial addr:x value:x deferred kind"),
        (DsEv.LLAND, "serial"),
        (DsEv.RRW, "waited_serial committed_serial completed_serial wait_ns reason addr:x"),

        (DsEv.ROOTS, "j:x s:x g:x s48:x s4c:x sb90 rtpp:x ctx:x disp:x ok_bits:x"),
        (DsEv.SRT, "old new rt:x wait_accum"),
        (DsEv.SFC, "i ctr label0:x label1:x exp0:x exp1:x lp0:x lp1:x"),
        (DsEv.SWK, "w cur:x fiber:x q08:x q10:x q58:x q60:x flags_aff last_job_tsc mailbox:x"),
        (DsEv.SLK, "ef8:x f78:x"),
        (DsEv.CSAT, "job:x counter:x value target cond suspend_ts eligible_idle idle_mask:x"),
        (DsEv.JD, "key:x chunk q0:x q1:x q2:x q3:x q4:x q5:x q6:x q7:x q8:x q9:x q10:x"),
        (DsEv.SCNT, "c2_0_3:x c2_4_7:x c2_8_11:x c2_12:x c3_0_3:x c3_4_7:x c3_8_11:x c3_12:x"),
        (DsEv.SGRP, "g sum_count waiting_full external_full"),
        (DsEv.SPOOL, "ec8 ef0 ed0"),
        (DsEv.SFM, "mode r34 r2c hz"),
        (DsEv.SSTAT, "ticks max_gap_ns p99_gap_ns avg_tick_cost_ns"),

        (DsEv.THR, "kind port thread_id guest:x name:s worker_idx"),
        (DsEv.TATTR, "qos_apply qos_class relpri policy sched_prio managed_priority"),
        (DsEv.CLK, "sw_mid tsc mat_ns ca_ns gap_ns"),
        (DsEv.PROC, "user_ns sys_ns p_user_ns p_sys_ns runnable_ns instr cycles intr_wkups idle_wkups footprint load1x100 nthreads"),
        (DsEv.OTHER, "others_cpu_ns pid1 cpu1 name1:s pid2 cpu2 name2:s pid3 cpu3 name3:s"),
        (DsEv.TCPU, "thread_id user_us sys_us cpu_usage run_state flags name:s port"),
        (DsEv.WCPU, "d0_1 d2_3 d4_5 d6_7 d8_9 d10_11 d12_13 running_mask"),
        (DsEv.GC, "gen0 gen1 gen2 pause_total_ns alloc_bytes lock_contention threadpool_threads"),
        (DsEv.AB, "phase window_idx"),

        (DsEv.WSTAT, "written dropped max_occupancy writer_cpu_ns"),
    ];

    internal readonly struct Entry(byte[] name, int fieldCount, ushort hexMask, string fields)
    {
        public readonly byte[] Name = name;
        public readonly int FieldCount = fieldCount;
        public readonly ushort HexMask = hexMask;
        public readonly string Fields = fields;
    }

    internal static readonly Entry[] Entries = Build();

    private static Entry[] Build()
    {
        var entries = new Entry[(int)DsEv.Count];
        foreach (var (type, fields) in _table)
        {
            var names = fields.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (names.Length > 13)
            {
                throw new InvalidOperationException($"DSTRACE schema {type} has {names.Length} fields; the slot holds 13.");
            }

            ushort mask = 0;
            for (var i = 0; i < names.Length; i++)
            {
                if (names[i].EndsWith(":x", StringComparison.Ordinal))
                {
                    mask |= (ushort)(1 << i);
                }
            }

            entries[(int)type] = new Entry(System.Text.Encoding.ASCII.GetBytes(type.ToString()), names.Length, mask, fields);
        }

        return entries;
    }

    internal static IEnumerable<(DsEv Type, string Fields)> Table => _table;
}
