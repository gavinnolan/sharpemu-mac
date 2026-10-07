#!/usr/bin/env python3
"""Read a stopped process's fixed native trace; report unpublished/missing tail."""
import argparse
import json
import pathlib
import struct
import uuid

MAGIC = 0x53454D554641554C
RECORDS = {1: struct.Struct('<4q7Q3q10i'), 2: struct.Struct('<4q7Q3q12i')}
FIELDS = ['published', 'sequence', 'nanoseconds', 'invocation', 'rawAddress',
          'address', 'errorCode', 'rip', 'ownerAddress', 'ownerEnd', 'size',
          'ownerIdentity', 'armEpoch', 'operation', 'kind', 'signal', 'signalCode', 'access',
          'ownerState', 'protection', 'result', 'errno', 'ownerObservation', 'reserved', 'route', 'reason']
KINDS = {1: 'early-tracker-entry', 2: 'early-tracker-result', 3: 'protection-begin',
         4: 'protection-return', 5: 'range-state', 6: 'range-retire', 7: 'final-disposition'}
ROUTES = {1: 'early-image-tracker', 2: 'gpu-fast-path', 3: 'raw-core-callback',
          4: 'core-recovery-callback', 5: 'no-core-callback', 6: 'bridge-exception'}
REASONS = {0: 'recovered', 1: 'declined', 2: 'missing-context', 3: 'caught-handler-exception', 4: 'preflight-warmup'}


def read_trace(path):
    data = pathlib.Path(path).read_bytes()
    if len(data) < 128:
        raise ValueError('truncated header')
    magic, version, header, size, capacity, page_size, architecture = struct.unpack_from('<Q6i', data)
    if magic != MAGIC or version not in RECORDS or header != 128:
        raise ValueError('unknown native trace schema')
    record_format = RECORDS[version]
    if size != record_format.size:
        raise ValueError('unknown native trace record size')
    if not 0 < capacity <= 4096 or len(data) != header + capacity * size:
        raise ValueError('invalid trace bounds')
    if page_size not in (4096, 16384) or architecture not in (0, 1, 2, 3):
        raise ValueError('unknown page size or process architecture')
    if data[32:48] == bytes(16):
        raise ValueError('missing session identity')
    records = []
    unpublished = 0
    for index in range(capacity):
        values = record_format.unpack_from(data, header + index * size)
        if values[0] == 0:
            unpublished += 1
            continue
        if values[0] != 1 or values[1] != index + 1 or values[14] not in KINDS:
            raise ValueError('invalid published record')
        record = dict(zip(FIELDS, values))
        record['event'] = 'native-signal-entry' if version >= 2 and record['kind'] == 1 else KINDS[record['kind']]
        if record['kind'] == 7:
            if version < 2 or record['route'] not in ROUTES or record['reason'] not in REASONS:
                raise ValueError('unknown final disposition')
            record['recoveryRoute'] = ROUTES[record['route']]
            record['dispositionReason'] = REASONS[record['reason']]
        records.append(record)
    invocations = []
    invocation_ids = sorted({record['invocation'] for record in records if record['invocation'] > 0})
    for invocation in invocation_ids:
        paired = [record for record in records if record['invocation'] == invocation]
        entries = [record['sequence'] for record in paired if record['kind'] == 1]
        early = [record['sequence'] for record in paired if record['kind'] == 2]
        final = [record['sequence'] for record in paired if record['kind'] == 7]
        invocations.append({'invocation': invocation, 'entrySequences': entries,
                            'earlyResultSequences': early, 'bridgeFinalSequences': final,
                            'bridgeFinalStatus': 'not-collected-schema1' if version == 1 else
                            'recorded' if len(final) == 1 else 'missing' if not final else 'duplicate'})
    return {'schemaVersion': version, 'session': str(uuid.UUID(bytes_le=data[32:48])),
            'recordBytes': size, 'capacity': capacity, 'nativePageBytes': page_size,
            'processArchitectureEnum': architecture,
            'dropped': struct.unpack_from('<q', data, 48)[0],
            'unpublishedSlots': unpublished, 'records': records, 'invocations': invocations,
            'limits': ['sequence orders diagnostic observations, not original kernel traps',
                       'timestamps precede owner-field observations; fields are not an atomic owner reservation',
                       'published MAP_SHARED slots are not a fatal-tail durability guarantee',
                       'owner observations never authorize recovery',
                       'final disposition is the emulator POSIX bridge outcome before previous-handler chaining',
                       'missing final records are unknown; abrupt callback termination, recursion or diagnostic loss can omit them',
                       'warmup success labels preflight control flow, not repair of a real kernel fault']}


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
