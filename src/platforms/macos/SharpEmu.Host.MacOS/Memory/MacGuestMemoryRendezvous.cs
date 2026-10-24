// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Host;

namespace SharpEmu.Host.MacOS;

/// <summary>
/// Hands a shareable guest backing and its view table to another process over Mach. The owner
/// checks in a bootstrap service under a name unique to it; a process told that name looks it up,
/// sends a request with a reply port, and gets back the backing as port rights: the chunked
/// backing's named-entry ports as they are, and the shm descriptor and the view table's descriptor
/// as fileports (<c>fileport_makeport</c>), which it turns back into descriptors. Rights are kernel
/// objects, so they cross between an x86-64 process under Rosetta and a native arm64 one unchanged.
///
/// <para>
/// One request, one reply: a complex message with one out-of-line ports descriptor (the view table,
/// then the backing in offset order) and the export's sizes inline. The reply goes to a send-once
/// right, which the kernel always queues, so the server never blocks on a slow or dead client.
/// Rights are copied, never moved: the owner keeps every port it sends, and the fileports it makes
/// for one reply are dropped after it.
/// </para>
/// </summary>
internal static unsafe partial class MacGuestMemoryRendezvous
{
    private const int RequestId = 0x5345_4D01;
    private const int ReplyId = 0x5345_4D02;
    private const uint ProtocolMagic = 0x4D45_4753; // "SGEM"

    private const int KERN_SUCCESS = 0;
    private const int TASK_BOOTSTRAP_PORT = 4;
    private const int MACH_PORT_RIGHT_RECEIVE = 1;
    private const int MACH_SEND_MSG = 0x1;
    private const int MACH_RCV_MSG = 0x2;
    private const int MACH_SEND_TIMEOUT = 0x10;
    private const int MACH_RCV_TIMEOUT = 0x100;
    private const int MACH_RCV_TIMED_OUT = 0x10004003;
    private const int MACH_RCV_TOO_LARGE = 0x10004004;
    private const int MACH_SEND_TIMED_OUT = 0x10000004;
    private const uint MACH_MSG_TYPE_MOVE_SEND_ONCE = 18;
    private const uint MACH_MSG_TYPE_COPY_SEND = 19;
    private const uint MACH_MSG_TYPE_MAKE_SEND_ONCE = 21;
    private const uint MACH_MSGH_BITS_COMPLEX = 0x8000_0000;
    private const uint MACH_MSG_PHYSICAL_COPY = 0;
    private const uint MACH_MSG_OOL_PORTS_DESCRIPTOR = 2;

    // Room for any reply plus the largest trailer a receive can append.
    private const int BufferBytes = 1024;

    private static int _serviceCounter;

    private static uint Bits(uint remote, uint local) => remote | (local << 8);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct MessageHeader
    {
        public uint Bits;
        public uint Size;
        public uint RemotePort;
        public uint LocalPort;
        public uint VoucherPort;
        public int Id;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct Request
    {
        public MessageHeader Header;
        public uint Magic;
    }

    // mach_msg_body_t, then mach_msg_ool_ports_descriptor_t (address; deallocate, copy, disposition
    // and type a byte each from the low end; count), then the export, all at 4-byte packing.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct Reply
    {
        public MessageHeader Header;
        public uint DescriptorCount;
        public ulong PortsAddress;
        public uint PortsFlags;
        public uint PortsCount;
        public uint Magic;
        public uint Kind;
        public ulong Size;
        public ulong MappedSize;
        public ulong ChunkBytes;
        public ulong ObjectBytes;
        public ulong ViewTableBytes;
    }

    /// <summary>Starts serving the export and view table; the caller keeps both alive until the server is disposed.</summary>
    public static Server Serve(GuestBackingExport backing, HostSharedRegion viewTable)
    {
        ArgumentNullException.ThrowIfNull(backing);
        ArgumentNullException.ThrowIfNull(viewTable);
        var task = mach_task_self();
        var name = $"com.sharpemu.guest-memory.{Environment.ProcessId}.{Interlocked.Increment(ref _serviceCounter)}";
        if (task_get_special_port(task, TASK_BOOTSTRAP_PORT, out var bootstrap) != KERN_SUCCESS)
            throw new InvalidOperationException("This process has no bootstrap port.");
        int checkedIn;
        uint port;
        try { checkedIn = bootstrap_check_in(bootstrap, name, out port); }
        finally { mach_port_deallocate(task, bootstrap); }
        if (checkedIn != KERN_SUCCESS)
            throw new InvalidOperationException($"bootstrap_check_in({name}) failed: 0x{checkedIn:X}.");
        return new Server(name, port, backing, viewTable);
    }

    /// <summary>Asks the service <paramref name="serviceName"/> for its guest memory, waiting for it to appear.</summary>
    public static GuestMemoryShare Connect(string serviceName, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrEmpty(serviceName);
        var task = mach_task_self();
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        if (task_get_special_port(task, TASK_BOOTSTRAP_PORT, out var bootstrap) != KERN_SUCCESS)
            throw new InvalidOperationException("This process has no bootstrap port.");
        uint server = 0, reply = 0;
        var buffer = (byte*)NativeMemory.AllocZeroed(BufferBytes);
        try
        {
            // The owner may still be starting: its name appears when it checks in.
            int found;
            while ((found = bootstrap_look_up(bootstrap, serviceName, out server)) != KERN_SUCCESS)
            {
                if (Environment.TickCount64 >= deadline)
                    throw new TimeoutException($"bootstrap_look_up({serviceName}) failed: 0x{found:X}.");
                Thread.Sleep(5);
            }

            if (mach_port_allocate(task, MACH_PORT_RIGHT_RECEIVE, out reply) != KERN_SUCCESS)
                throw new InvalidOperationException("Could not allocate a reply port.");
            var request = (Request*)buffer;
            request->Header.Bits = Bits(MACH_MSG_TYPE_COPY_SEND, MACH_MSG_TYPE_MAKE_SEND_ONCE);
            request->Header.Size = (uint)sizeof(Request);
            request->Header.RemotePort = server;
            request->Header.LocalPort = reply;
            request->Header.Id = RequestId;
            request->Magic = ProtocolMagic;
            var wait = (uint)Math.Clamp(deadline - Environment.TickCount64, 1, int.MaxValue);
            var result = mach_msg(&request->Header, MACH_SEND_MSG | MACH_RCV_MSG | MACH_SEND_TIMEOUT | MACH_RCV_TIMEOUT,
                (uint)sizeof(Request), BufferBytes, reply, wait, 0);
            if (result == MACH_SEND_TIMED_OUT) mach_msg_destroy(&request->Header);
            if (result == MACH_RCV_TIMED_OUT) throw new TimeoutException($"{serviceName} did not answer.");
            if (result != KERN_SUCCESS) throw new InvalidOperationException($"mach_msg to {serviceName} failed: 0x{result:X}.");
            return Receive((Reply*)buffer, serviceName);
        }
        finally
        {
            NativeMemory.Free(buffer);
            if (reply != 0) mach_port_mod_refs(task, reply, MACH_PORT_RIGHT_RECEIVE, -1);
            if (server != 0) mach_port_deallocate(task, server);
            mach_port_deallocate(task, bootstrap);
        }
    }

    // Takes the rights out of a received reply. Anything malformed is destroyed with its rights.
    private static GuestMemoryShare Receive(Reply* reply, string serviceName)
    {
        var task = mach_task_self();
        if (reply->Header.Id != ReplyId || (reply->Header.Bits & MACH_MSGH_BITS_COMPLEX) == 0 ||
            reply->Header.Size < (uint)sizeof(Reply) || reply->DescriptorCount != 1 || reply->Magic != ProtocolMagic ||
            reply->PortsFlags >> 24 != MACH_MSG_OOL_PORTS_DESCRIPTOR || reply->PortsCount < 2 ||
            (reply->Kind != (uint)GuestBackingExportKind.SharedMemoryObject && reply->Kind != (uint)GuestBackingExportKind.MemoryEntries))
        {
            mach_msg_destroy(&reply->Header);
            throw new InvalidOperationException($"{serviceName} sent a malformed guest memory reply.");
        }

        var count = (int)reply->PortsCount;
        var names = new uint[count];
        new ReadOnlySpan<uint>((void*)reply->PortsAddress, count).CopyTo(names);
        mach_vm_deallocate(task, reply->PortsAddress, (ulong)count * sizeof(uint));
        var kind = (GuestBackingExportKind)reply->Kind;
        int viewTable = -1, descriptor = -1;
        var entries = new List<GuestBackingEntry>();
        try
        {
            viewTable = TakeDescriptor(task, ref names[0]);
            if (kind == GuestBackingExportKind.SharedMemoryObject)
            {
                descriptor = TakeDescriptor(task, ref names[1]);
            }
            else
            {
                var objectBytes = reply->ObjectBytes;
                if (objectBytes == 0 || (ulong)(count - 1) != (reply->MappedSize + objectBytes - 1) / objectBytes)
                    throw new InvalidOperationException($"{serviceName} sent {count - 1} objects for 0x{reply->MappedSize:X} bytes.");
                for (var index = 1; index < count; index++)
                {
                    var offset = (ulong)(index - 1) * objectBytes;
                    entries.Add(new GuestBackingEntry(offset, Math.Min(objectBytes, reply->MappedSize - offset), names[index]));
                    names[index] = 0;
                }
            }

            var export = new GuestBackingExport(kind, reply->Size, reply->MappedSize, descriptor, entries, reply->ChunkBytes,
                reply->ObjectBytes);
            var viewTableBytes = reply->ViewTableBytes;
            return new GuestMemoryShare(export, viewTable, viewTableBytes, static share =>
            {
                var owner = mach_task_self();
                close(share.ViewTableDescriptor);
                if (share.Backing.Descriptor >= 0) close(share.Backing.Descriptor);
                foreach (var entry in share.Backing.Entries) mach_port_deallocate(owner, entry.Handle);
            });
        }
        catch
        {
            if (viewTable >= 0) close(viewTable);
            if (descriptor >= 0) close(descriptor);
            foreach (var entry in entries) mach_port_deallocate(task, entry.Handle);
            foreach (var name in names)
                if (name != 0) mach_port_deallocate(task, name);
            throw;
        }
    }

    private static int TakeDescriptor(uint task, ref uint fileport)
    {
        var descriptor = fileport_makefd(fileport);
        mach_port_deallocate(task, fileport);
        fileport = 0;
        if (descriptor < 0) throw new InvalidOperationException($"fileport_makefd failed: errno {Marshal.GetLastPInvokeError()}.");
        return descriptor;
    }

    /// <summary>The owner's side: answers every request until disposed.</summary>
    internal sealed class Server : IDisposable
    {
        // How often the serving thread looks up from an empty port to see whether it should stop.
        private const uint PollMilliseconds = 100;

        private readonly uint _port;
        private readonly GuestBackingExport _backing;
        private readonly HostSharedRegion _viewTable;
        private readonly Thread _thread;
        private int _stopping;
        private int _served;

        internal Server(string name, uint port, GuestBackingExport backing, HostSharedRegion viewTable)
        {
            ServiceName = name;
            _port = port;
            _backing = backing;
            _viewTable = viewTable;
            _thread = new Thread(Run) { IsBackground = true, Name = "SharpEmu guest memory rendezvous" };
            _thread.Start();
        }

        public string ServiceName { get; }

        /// <summary>Replies sent so far.</summary>
        public int Served => Volatile.Read(ref _served);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
            _thread.Join();
            // Gone with the receive right: later lookups of the name fail.
            mach_port_mod_refs(mach_task_self(), _port, MACH_PORT_RIGHT_RECEIVE, -1);
        }

        private void Run()
        {
            var buffer = (byte*)NativeMemory.AllocZeroed(BufferBytes);
            try
            {
                while (Volatile.Read(ref _stopping) == 0)
                {
                    var header = (MessageHeader*)buffer;
                    var result = mach_msg(header, MACH_RCV_MSG | MACH_RCV_TIMEOUT, 0, BufferBytes, _port, PollMilliseconds, 0);
                    if (result is MACH_RCV_TIMED_OUT or MACH_RCV_TOO_LARGE) continue;
                    if (result != KERN_SUCCESS) break;
                    var request = (Request*)buffer;
                    if (header->Id != RequestId || header->Size < (uint)sizeof(Request) || request->Magic != ProtocolMagic ||
                        header->RemotePort == 0 || !TryAnswer(header->RemotePort))
                        mach_msg_destroy(header);
                }
            }
            finally { NativeMemory.Free(buffer); }
        }

        // Sends the reply to the request's send-once right, consuming it on success.
        private bool TryAnswer(uint replyPort)
        {
            var task = mach_task_self();
            var shm = _backing.Kind == GuestBackingExportKind.SharedMemoryObject;
            var names = new uint[1 + (shm ? 1 : _backing.Entries.Count)];
            try
            {
                if (fileport_makeport(_viewTable.Descriptor, out names[0]) != 0) return false;
                if (shm)
                {
                    if (fileport_makeport(_backing.Descriptor, out names[1]) != 0) return false;
                }
                else
                {
                    for (var index = 0; index < _backing.Entries.Count; index++) names[index + 1] = _backing.Entries[index].Handle;
                }

                Reply reply = default;
                reply.Header.Bits = Bits(MACH_MSG_TYPE_MOVE_SEND_ONCE, 0) | MACH_MSGH_BITS_COMPLEX;
                reply.Header.Size = (uint)sizeof(Reply);
                reply.Header.RemotePort = replyPort;
                reply.Header.Id = ReplyId;
                reply.DescriptorCount = 1;
                reply.PortsFlags = (MACH_MSG_PHYSICAL_COPY << 8) | (MACH_MSG_TYPE_COPY_SEND << 16) | (MACH_MSG_OOL_PORTS_DESCRIPTOR << 24);
                reply.PortsCount = (uint)names.Length;
                reply.Magic = ProtocolMagic;
                reply.Kind = (uint)_backing.Kind;
                reply.Size = _backing.Size;
                reply.MappedSize = _backing.MappedSize;
                reply.ChunkBytes = _backing.ChunkBytes;
                reply.ObjectBytes = _backing.ObjectBytes;
                reply.ViewTableBytes = _viewTable.Size;
                int result;
                fixed (uint* ports = names)
                {
                    reply.PortsAddress = (ulong)ports;
                    result = mach_msg(&reply.Header, MACH_SEND_MSG, (uint)sizeof(Reply), 0, 0, 0, 0);
                }

                if (result != KERN_SUCCESS) return false;
                Interlocked.Increment(ref _served);
                return true;
            }
            finally
            {
                // The fileports were made for this reply only; the entry ports stay the backing's.
                if (names[0] != 0) mach_port_deallocate(task, names[0]);
                if (shm && names[1] != 0) mach_port_deallocate(task, names[1]);
            }
        }
    }

    [LibraryImport("libSystem.B.dylib")]
    private static partial uint mach_task_self();

    [LibraryImport("libSystem.B.dylib")]
    private static partial int task_get_special_port(uint task, int which, out uint port);

    [LibraryImport("libSystem.B.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int bootstrap_check_in(uint bootstrapPort, string serviceName, out uint port);

    [LibraryImport("libSystem.B.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int bootstrap_look_up(uint bootstrapPort, string serviceName, out uint port);

    [LibraryImport("libSystem.B.dylib")]
    private static partial int mach_port_allocate(uint task, int right, out uint name);

    [LibraryImport("libSystem.B.dylib")]
    private static partial int mach_port_deallocate(uint task, uint name);

    [LibraryImport("libSystem.B.dylib")]
    private static partial int mach_port_mod_refs(uint task, uint name, int right, int delta);

    [LibraryImport("libSystem.B.dylib")]
    private static partial int mach_msg(MessageHeader* message, int option, uint sendSize, uint receiveSize, uint receivePort,
        uint timeout, uint notify);

    [LibraryImport("libSystem.B.dylib")]
    private static partial void mach_msg_destroy(MessageHeader* message);

    [LibraryImport("libSystem.B.dylib")]
    private static partial int mach_vm_deallocate(uint task, ulong address, ulong size);

    [LibraryImport("libSystem.B.dylib", SetLastError = true)]
    private static partial int fileport_makeport(int descriptor, out uint port);

    [LibraryImport("libSystem.B.dylib", SetLastError = true)]
    private static partial int fileport_makefd(uint port);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int descriptor);
}
