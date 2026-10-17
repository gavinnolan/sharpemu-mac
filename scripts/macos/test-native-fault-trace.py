#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Offline schema compatibility and fatal-tail tests; no application/device."""
import importlib.util
import pathlib
import struct
import tempfile
import unittest
import uuid

spec = importlib.util.spec_from_file_location('native_trace', pathlib.Path(__file__).with_name('read-native-fault-trace.py'))
trace = importlib.util.module_from_spec(spec)
spec.loader.exec_module(trace)


class NativeTraceReaderTests(unittest.TestCase):
    def read(self, version, records):
        fmt = trace.RECORDS[version]
        capacity = len(records) + 1
        data = bytearray(128 + capacity * fmt.size)
        struct.pack_into('<Q6i', data, 0, trace.MAGIC, version, 128, fmt.size, capacity, 4096, 1)
        data[32:48] = uuid.uuid4().bytes_le
        for index, fields in enumerate(records):
            values = {field: 0 for field in trace.FIELDS}
            values.update(published=1, sequence=index + 1, invocation=7)
            values.update(fields)
            fmt.pack_into(data, 128 + index * fmt.size,
                          *(values[field] for field in trace.FIELDS[:len(fmt.unpack(bytes(fmt.size)))]))
        with tempfile.TemporaryDirectory() as folder:
            path = pathlib.Path(folder, 'trace.bin')
            path.write_bytes(data)
            return trace.read_trace(path)

    def test_retained_schema1_has_no_final_collection_claim(self):
        parsed = self.read(1, [{'kind': 1}, {'kind': 2, 'result': 0}])
        self.assertEqual(152, parsed['recordBytes'])
        self.assertEqual('not-collected-schema1', parsed['invocations'][0]['bridgeFinalStatus'])
        self.assertEqual(1, parsed['unpublishedSlots'])

    def test_schema2_pairs_declined_early_with_recovered_bridge(self):
        parsed = self.read(2, [{'kind': 1, 'signal': 4}, {'kind': 2, 'result': 0},
                              {'kind': 7, 'result': 1, 'route': 4, 'reason': 0}])
        self.assertEqual(160, parsed['recordBytes'])
        self.assertEqual('native-signal-entry', parsed['records'][0]['event'])
        self.assertEqual('core-recovery-callback', parsed['records'][2]['recoveryRoute'])
        self.assertEqual('recorded', parsed['invocations'][0]['bridgeFinalStatus'])
        self.assertEqual([3], parsed['invocations'][0]['bridgeFinalSequences'])

    def test_missing_final_is_unknown_instead_of_declined(self):
        parsed = self.read(2, [{'kind': 1}, {'kind': 2, 'result': 0}])
        self.assertEqual('missing', parsed['invocations'][0]['bridgeFinalStatus'])
        self.assertEqual([], parsed['invocations'][0]['bridgeFinalSequences'])

    def test_unpublished_fatal_tail_is_never_decoded(self):
        parsed = self.read(2, [{'kind': 1}, {'published': 0, 'kind': 999}])
        self.assertEqual(1, len(parsed['records']))
        self.assertEqual(2, parsed['unpublishedSlots'])
        self.assertEqual('missing', parsed['invocations'][0]['bridgeFinalStatus'])

    def test_duplicate_final_remains_visible(self):
        parsed = self.read(2, [{'kind': 1}, {'kind': 7, 'route': 5, 'reason': 1},
                              {'kind': 7, 'route': 5, 'reason': 1}])
        self.assertEqual('duplicate', parsed['invocations'][0]['bridgeFinalStatus'])

    def test_unknown_disposition_is_rejected(self):
        for fields in ({'route': 99, 'reason': 0}, {'route': 3, 'reason': 99}):
            with self.subTest(fields=fields), self.assertRaisesRegex(ValueError, 'unknown final disposition'):
                self.read(2, [{'kind': 7, **fields}])

    def test_schema3_range_cas_state_is_distinct_from_owner_observation(self):
        parsed = self.read(3, [{'kind': 1, 'address': 0x8120},
                              {'kind': 8, 'address': 0x8120, 'route': 1, 'reason': 3,
                               'reserved': 2, 'ownerState': 0, 'ownerIdentity': 11,
                               'armEpoch': 8, 'ownerObservation': 2, 'size': 4096, 'result': 1,
                               'rawAddress': 16, 'errorCode': 0x8000, 'ownerAddress': 0x8120, 'ownerEnd': 0x9000},
                              {'kind': 2, 'result': 0}, {'kind': 7, 'route': 4, 'reason': 1}])
        decision = parsed['records'][1]
        self.assertEqual(160, parsed['recordBytes'])
        self.assertEqual('busy-invalidating', decision['decisionReason'])
        self.assertEqual('invalidating', decision['casPreviousStateName'])
        self.assertEqual('disarmed', decision['observedOwnerStateName'])
        self.assertTrue(decision['branchHandled'])
        self.assertEqual(4096, decision['trackingPageBytes'])
        self.assertEqual(16, decision['requestedRangeBytes'])
        self.assertEqual(0x8130, decision['requestedRangeEnd'])
        self.assertEqual(0x8000, decision['trackingStart'])
        self.assertEqual(0x9000, decision['trackingEnd'])
        self.assertEqual([2], parsed['invocations'][0]['rangeDecisionSequences'])
        self.assertEqual('recorded', parsed['invocations'][0]['bridgeFinalStatus'])
        self.assertEqual(0, parsed['records'][2]['result']) # branch is not final acceptance

    def test_schema3_exact_range_end_saturates_like_tracker_predicate(self):
        record = self.read(3, [{'kind': 8, 'route': 1, 'reason': 5,
                               'ownerAddress': 0xFFFFFFFFFFFFFFF0, 'rawAddress': 32}])['records'][0]
        self.assertEqual(0xFFFFFFFFFFFFFFFF, record['requestedRangeEnd'])

    def test_schema3_all_range_reasons_and_passes_decode(self):
        for reason, name in trace.RANGE_REASONS.items():
            for route, pass_name in trace.RANGE_PASSES.items():
                with self.subTest(reason=reason, route=route):
                    record = self.read(3, [{'kind': 8, 'route': route, 'reason': reason}])['records'][0]
                    self.assertEqual(name, record['decisionReason'])
                    self.assertEqual(pass_name, record['decisionPass'])

    def test_schema2_cannot_claim_new_range_decisions(self):
        with self.assertRaisesRegex(ValueError, 'unknown tracker range decision'):
            self.read(2, [{'kind': 8, 'route': 1, 'reason': 3}])
        old = self.read(2, [{'kind': 1}])
        self.assertEqual('not-collected-before-schema3', old['invocations'][0]['rangeDecisionStatus'])

    def test_schema3_missing_range_decisions_are_not_inferred(self):
        parsed = self.read(3, [{'kind': 1}, {'kind': 2, 'result': 0}])
        self.assertEqual('missing', parsed['invocations'][0]['rangeDecisionStatus'])
        self.assertEqual('missing', parsed['invocations'][0]['bridgeFinalStatus'])

    def test_schema3_rejects_unknown_range_enums(self):
        for fields in ({'route': 99, 'reason': 3}, {'route': 1, 'reason': 99}):
            with self.subTest(fields=fields), self.assertRaisesRegex(ValueError, 'unknown tracker range decision'):
                self.read(3, [{'kind': 8, **fields}])

    def test_schema4_retains_observed_zero_and_full_width_raw_trap_bits(self):
        for trap in (0, 6, 13, 14, 65535, 0x8000000000000000, 0xFFFFFFFFFFFFFFFF):
            signed = trap if trap < (1 << 63) else trap - (1 << 64)
            with self.subTest(trap=trap):
                parsed = self.read(4, [{'kind': 1, 'operation': signed, 'reserved': 1, 'errorCode': 6}])
                entry = parsed['records'][0]
                self.assertEqual(160, parsed['recordBytes'])
                self.assertEqual('observed-context-field', entry['trapNumberStatus'])
                self.assertEqual(trap, entry['trapNumber'])
                self.assertEqual(6, entry['errorCode'])

    def test_schema4_missing_context_is_unobserved_not_zero(self):
        entry = self.read(4, [{'kind': 1}])['records'][0]
        self.assertEqual('unobserved', entry['trapNumberStatus'])
        self.assertIsNone(entry['trapNumber'])

    def test_old_schemas_never_infer_trap_from_reused_zero_or_scalars(self):
        for version in (1, 2, 3):
            for fields in ({}, {'operation': 14, 'reserved': 1}):
                with self.subTest(version=version, fields=fields):
                    entry = self.read(version, [{'kind': 1, **fields}])['records'][0]
                    self.assertEqual('not-collected-before-schema4', entry['trapNumberStatus'])
                    self.assertIsNone(entry['trapNumber'])

    def test_schema4_rejects_invalid_or_contradictory_trap_availability(self):
        for fields in ({'reserved': -1}, {'reserved': 2}, {'reserved': 0, 'operation': 14}):
            with self.subTest(fields=fields), self.assertRaisesRegex(ValueError, 'invalid trap-number availability'):
                self.read(4, [{'kind': 1, **fields}])

    def test_schema4_keeps_range_cas_and_protection_operation_separate_from_trap(self):
        parsed = self.read(4, [{'kind': 1, 'operation': 13, 'reserved': 1},
                              {'kind': 8, 'route': 1, 'reason': 3, 'reserved': 2, 'operation': 14},
                              {'kind': 3, 'operation': 14}, {'kind': 4, 'operation': 14},
                              {'kind': 7, 'route': 4, 'reason': 1}])
        self.assertEqual(13, parsed['records'][0]['trapNumber'])
        self.assertEqual(2, parsed['records'][1]['casPreviousState'])
        self.assertEqual(14, parsed['records'][2]['operation'])
        self.assertEqual(14, parsed['records'][3]['operation'])
        for record in parsed['records'][1:]:
            self.assertNotIn('trapNumber', record)
        self.assertEqual('recorded', parsed['invocations'][0]['bridgeFinalStatus'])

    def test_schema4_unpublished_entry_does_not_supply_a_trap_to_later_records(self):
        parsed = self.read(4, [{'published': 0, 'kind': 1, 'operation': 14, 'reserved': 1},
                              {'kind': 2, 'result': 0}, {'kind': 7, 'route': 4, 'reason': 1}])
        self.assertEqual([], parsed['invocations'][0]['entrySequences'])
        for record in parsed['records']:
            self.assertNotIn('trapNumber', record)


if __name__ == '__main__':
    unittest.main()
