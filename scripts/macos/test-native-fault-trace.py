#!/usr/bin/env python3
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


if __name__ == '__main__':
    unittest.main()
