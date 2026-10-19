# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
import unittest
from metal_drain_allocations import parse_log

HEADER = '[PERF][METAL_DRAIN_ALLOC_TRACE] schema=1 retained=1 overwritten=0 scope=current_presenter_thread_allocated_bytes'
VALUES = 'allocated_bytes=4096 drain_calls=2 completed_work=3 completed_draws=1 completed_compute=1 failed_drains=0 discarded_scopes=0'
ROW = '[PERF][METAL_DRAIN_ALLOC_FRAME] sequence=0 guest_sequence=43 ' + VALUES
TAIL = '[PERF][METAL_DRAIN_ALLOC_TAIL] scope=since_last_guest_commit ' + VALUES
WALL = '[PERF][METAL_PRESENT_FRAME] sequence=0 timestamp=234 gap_ms=16.7 DrainGuestWork=5.2 MaterializePresentation=0.01 AcquireDrawable=10.8 EncodeAndCommit=0.17'


class DecoderTests(unittest.TestCase):
    def test_existing_timing_only_trace_is_uncollected_not_zero(self):
        result = parse_log(WALL)
        self.assertFalse(result['allocation_available'])
        self.assertEqual(result['legacy_wall_frames'], 1)
        self.assertIsNone(result['tail'])

    def test_new_rows_correlate_by_ordinal_without_changing_wall_fields(self):
        result = parse_log('\n'.join([HEADER, ROW, WALL, TAIL]))
        frame = result['frames'][0]
        self.assertEqual(frame['guest_sequence'], 43)
        self.assertEqual(frame['allocated_bytes'], 4096)
        self.assertEqual(frame['presenter_wall_fields']['gap_ms'], '16.7')

    def test_allocation_only_has_no_fabricated_wall_clock(self):
        result = parse_log('\n'.join([HEADER, ROW, TAIL]))
        self.assertIsNone(result['frames'][0]['presenter_wall_fields'])

    def test_empty_no_ack_tail_is_not_divided_by_zero(self):
        text = HEADER.replace('retained=1', 'retained=0') + '\n' + TAIL.replace('completed_work=3 completed_draws=1 completed_compute=1', 'completed_work=0 completed_draws=0 completed_compute=0')
        result = parse_log(text)
        self.assertEqual(result['frames'], [])
        self.assertIsNone(result['tail']['bytes_per_acknowledged_work'])

    def test_partial_failure_and_discarded_scope_are_explicit(self):
        text = '\n'.join([HEADER, ROW.replace('failed_drains=0 discarded_scopes=0', 'failed_drains=1 discarded_scopes=1'), TAIL])
        row = parse_log(text)['frames'][0]
        self.assertEqual(row['failed_drains'], 1)
        self.assertEqual(row['allocation_coverage'], 'partial_discarded_scope')
        self.assertIsNone(row['bytes_per_acknowledged_work'])

    def test_overwritten_window_ordinals_are_preserved(self):
        text = '\n'.join([HEADER.replace('overwritten=0', 'overwritten=10'), ROW.replace('sequence=0 ', 'sequence=10 '), TAIL])
        self.assertEqual(parse_log(text)['frames'][0]['sequence'], 10)

    def test_missing_header_tail_or_rows_is_rejected(self):
        for text in [ROW, HEADER + '\n' + ROW, HEADER + '\n' + TAIL]:
            with self.subTest(text=text), self.assertRaises(ValueError):
                parse_log(text)

    def test_duplicate_session_or_ordinal_is_rejected(self):
        for text in ['\n'.join([HEADER, HEADER, ROW, TAIL]), '\n'.join([HEADER, ROW, ROW, TAIL]), '\n'.join([HEADER, ROW, TAIL, TAIL]), WALL + '\n' + WALL]:
            with self.subTest(text=text), self.assertRaises(ValueError):
                parse_log(text)

    def test_invalid_scope_version_count_and_counter_are_rejected(self):
        for text in [HEADER.replace('schema=1', 'schema=2'), HEADER.replace('retained=1', 'retained=4097'), HEADER.replace('current_presenter_thread_allocated_bytes', 'all_threads'), HEADER + '\n' + ROW.replace('allocated_bytes=4096', 'allocated_bytes=-1') + '\n' + TAIL, HEADER + '\n' + ROW.replace('completed_work=3', 'completed_work=0') + '\n' + TAIL]:
            with self.subTest(text=text), self.assertRaises(ValueError):
                parse_log(text)


if __name__ == '__main__':
    unittest.main()
