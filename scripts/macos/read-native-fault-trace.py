#!/usr/bin/env python3
"""Read a stopped process's fixed native trace; ignore unpublished fatal tail."""
import argparse
import json
import pathlib
import struct
import uuid

MAGIC = 0x53454D554641554C
RECORD = struct.Struct('<4q7Q3q10i')
FIELDS = ['published', 'sequence', 'nanoseconds', 'invocation', 'rawAddress',
          'address', 'errorCode', 'rip', 'ownerAddress', 'ownerEnd', 'size',
          'ownerIdentity', 'armEpoch', 'operation', 'kind', 'signal', 'signalCode', 'access',
          'ownerState', 'protection', 'result', 'errno', 'ownerObservation', 'reserved']
KINDS = {1: 'early-tracker-entry', 2: 'early-tracker-result', 3: 'protection-begin',
         4: 'protection-return', 5: 'range-state', 6: 'range-retire'}


def read_trace(path):
    data = pathlib.Path(path).read_bytes()
    if len(data) < 128:
        raise ValueError('truncated header')
    magic, version, header, size, capacity, page_size, architecture = struct.unpack_from('<Q6i', data)
    if magic != MAGIC or version != 1 or header != 128 or size != RECORD.size:
        raise ValueError('unknown native trace schema')
    if not 0 < capacity <= 4096 or len(data) != header + capacity * size:
        raise ValueError('invalid trace bounds')
    if page_size not in (4096, 16384) or architecture not in (0, 1, 2, 3):
        raise ValueError('unknown page size or process architecture')
    if data[32:48] == bytes(16):
        raise ValueError('missing session identity')
    records = []
    unpublished = 0
    for index in range(capacity):
        values = RECORD.unpack_from(data, header + index * size)
        if values[0] == 0:
            unpublished += 1
            continue
        if values[0] != 1 or values[1] != index + 1 or values[14] not in KINDS:
            raise ValueError('invalid published record')
        record = dict(zip(FIELDS, values))
        record['event'] = KINDS[record['kind']]
        records.append(record)
    return {'schemaVersion': version, 'session': str(uuid.UUID(bytes_le=data[32:48])),
            'recordBytes': size, 'capacity': capacity, 'nativePageBytes': page_size,
            'processArchitectureEnum': architecture,
            'dropped': struct.unpack_from('<q', data, 48)[0],
            'unpublishedSlots': unpublished, 'records': records,
            'limits': ['sequence orders diagnostic observations, not original kernel traps',
                       'timestamps precede owner-field observations; fields are not an atomic owner reservation',
                       'published MAP_SHARED slots are not a fatal-tail durability guarantee',
                       'owner observations never authorize recovery']}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('trace')
    parser.add_argument('--output')
    args = parser.parse_args()
    result = json.dumps(read_trace(args.trace), indent=2) + '\n'
    if args.output:
        pathlib.Path(args.output).write_text(result)
    else:
        print(result, end='')
