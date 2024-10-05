using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using static Quiche.NativeMethods;

#if WINDOWS
using size_t = int;
#else
using size_t = uint;
#endif

namespace Quiche.NET;

public class QuicheConnection : IDisposable
{
    private static (MemoryHandle, int) GetSocketAddress(EndPoint? endPoint)
    {
        Memory<byte>? buf = endPoint?.Serialize().Buffer;
        return buf is null ? default : (buf.Value.Pin(), buf.Value.Length);
    }

    internal unsafe static QuicheConnection Accept(Socket socket,
        EndPoint remoteEndPoint, ReadOnlyMemory<byte> initialData,
        QuicheConfig config, byte[]? cid = null)
    {
        EndPoint localEndPoint = socket.LocalEndPoint ?? throw new ArgumentException(
            "Given socket was not bound to a valid local endpoint!", nameof(socket));

        var (local, local_len) = GetSocketAddress(localEndPoint);
        var (remote, remote_len) = GetSocketAddress(remoteEndPoint);

        using (local)
        using (remote)
        {
            byte[] scidBuf = (byte[]?)cid?.Clone() ?? RandomNumberGenerator
                .GetBytes((int)QuicheLibrary.MAX_CONN_ID_LEN);
            fixed (byte* scidPtr = scidBuf)
            {
                return new(quiche_accept(
                    scidPtr, (nuint)scidBuf.Length, null, 0,
                    (sockaddr*)local.Pointer, (size_t)local_len,
                    (sockaddr*)remote.Pointer, (size_t)remote_len,
                    config.NativePtr),
                    socket, remoteEndPoint,
                    initialData, scidBuf);
            }
        }
    }

    public unsafe static QuicheConnection Connect(Socket socket, EndPoint remoteEndPoint,
        QuicheConfig config, string? hostname = null, byte[]? cid = null)
    {
        EndPoint localEndPoint = socket.LocalEndPoint ?? throw new ArgumentException(
            "Given socket was not bound to a valid local endpoint!", nameof(socket));

        var (local, local_len) = GetSocketAddress(localEndPoint);
        var (remote, remote_len) = GetSocketAddress(remoteEndPoint);

        using (local)
        using (remote)
        {
            byte[] hostnameBuf = Encoding.UTF8.GetBytes([.. hostname?.ToCharArray(), '\u0000']);
            byte[] scidBuf = (byte[]?)cid?.Clone() ?? RandomNumberGenerator
                .GetBytes((int)QuicheLibrary.MAX_CONN_ID_LEN);

            fixed (byte* hostnamePtr = hostnameBuf)
            fixed (byte* scidPtr = scidBuf)
            {
                return new(quiche_connect(hostnamePtr,
                    scidPtr, (nuint)scidBuf.Length,
                    (sockaddr*)local.Pointer, (size_t)local_len,
                    (sockaddr*)remote.Pointer, (size_t)remote_len,
                    config.NativePtr),
                    socket, remoteEndPoint,
                    ReadOnlyMemory<byte>.Empty, scidBuf);
            }
        }
    }

    private readonly Task? listenTask;
    private readonly Task recvTask, sendTask;
    private readonly CancellationTokenSource cts;

    private readonly TaskCompletionSource establishedTcs;
    private readonly ConcurrentDictionary<long, QuicheStream> streamMap;
    private readonly ConcurrentBag<TaskCompletionSource<QuicheStream>> streamBag;

    private readonly Socket socket;
    private readonly EndPoint remoteEndPoint;
    private readonly bool shouldCloseSocket;

    internal readonly ConcurrentQueue<(long, byte[])> sendQueue;
    internal readonly ConcurrentQueue<ReadOnlyMemory<byte>> recvQueue;

    private readonly byte[] connectionId;
    internal ReadOnlySpan<byte> ConnectionId => connectionId;

    internal unsafe Conn* NativePtr { get; private set; }

    public Task ConnectionEstablished => establishedTcs.Task;

    public unsafe bool IsClosed 
    {
        get 
        {
            lock (this)
            {
                return NativePtr is not null && NativePtr->IsClosed();
            }
        }
    }

    private unsafe QuicheConnection(Conn* nativePtr, Socket socket, EndPoint remoteEndPoint, ReadOnlyMemory<byte> initialData, ReadOnlyMemory<byte> connectionId)
    {
        NativePtr = nativePtr;

        this.socket = socket;
        this.remoteEndPoint = remoteEndPoint;

        this.connectionId = new byte[QuicheLibrary.MAX_CONN_ID_LEN];
        connectionId.CopyTo(this.connectionId);

        sendQueue = new();
        recvQueue = new();

        streamMap = new();
        streamBag = new();

        establishedTcs = new();

        cts = new();

        recvTask = Task.Run(() => ReceiveAsync(cts.Token));
        sendTask = Task.Run(() => SendAsync(cts.Token));

        if (initialData.IsEmpty)
        {
            listenTask = Task.Run(() => ListenAsync(cts.Token));
            shouldCloseSocket = true;
        }
        else
        {
            recvQueue.Enqueue(initialData);
            shouldCloseSocket = false;
        }
    }

    private class SendScheduleInfo
    {
        public int SendCount { get; set; }
        public byte[]? SendBuffer { get; set; }
    }

    private void SendPacket(object? state)
    {
        SendScheduleInfo? info = state as SendScheduleInfo;
        if (info is not null)
        {
            lock (info)
            {
                if (info.SendBuffer is not null)
                {
                    int bytesSent = 0;
                    while (bytesSent < info.SendCount)
                    {
                        var packetSpan = info.SendBuffer.AsSpan(bytesSent, info.SendCount - bytesSent);
                        bytesSent += socket.SendTo(packetSpan, remoteEndPoint);
                    }
                }
            }
        }
    }

    private async Task SendAsync(CancellationToken cancellationToken)
    {
        byte[] packetBuf = new byte[QuicheLibrary.MAX_DATAGRAM_LEN];

        SendScheduleInfo info = new() { SendBuffer = packetBuf };
        Timer timer = new Timer(SendPacket, info, Timeout.Infinite, Timeout.Infinite);

        while (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                long resultOrError = packetBuf.Length;
                unsafe
                {
                    lock (this)
                    {
                        if (NativePtr->IsEstablished() &&
                            (establishedTcs.TrySetResult() || ConnectionEstablished.IsCompleted) &&
                            sendQueue.TryDequeue(out (long streamId, byte[] buf) pair))
                        {
                            fixed (byte* bufPtr = pair.buf)
                            {
                                long errorCode = (long)QuicheError.QUICHE_ERR_NONE;
                                QuicheStream stream = GetStream(pair.streamId, false);
                                resultOrError = NativePtr->StreamSend(
                                    (ulong)pair.streamId, bufPtr, (nuint)pair.buf.Length,
                                    !stream.CanWrite, (ulong*)Unsafe.AsPointer(ref errorCode)
                                    );
                                QuicheException.ThrowIfError((QuicheError)errorCode, "An uncaught error occured in quiche!");
                            }
                            pair.buf.AsSpan(0, (int)resultOrError).CopyTo(info.SendBuffer.AsSpan());
                        }
                    }
                }

                await Task.Yield();

                SendInfo sendInfo = default;
                unsafe
                {
                    lock (this)
                    {
                        fixed (byte* pktPtr = info.SendBuffer)
                        {
                            resultOrError = (long)NativePtr->Send(
                                pktPtr, (nuint)resultOrError,
                                (SendInfo*)Unsafe.AsPointer(ref sendInfo)
                                );
                            QuicheException.ThrowIfError((QuicheError)resultOrError, "An uncaught error occured in quiche!");
                        }
                    }
                }

                lock (info)
                {
                    info.SendCount = (int)resultOrError;
                }

                timer.Change(
                    TimeSpan.FromSeconds(Unsafe.As<timespec, CLong>
                        (ref sendInfo.at).Value) +
                    TimeSpan.FromTicks(sendInfo.at.tv_nsec.Value / 100),
                    Timeout.InfiniteTimeSpan
                    );
            }
            catch (QuicheException ex) when
                (ex.ErrorCode == QuicheError.QUICHE_ERR_DONE)
            { await Task.Delay(75, cancellationToken); continue; }
            catch (QuicheException ex)
            {
                establishedTcs.TrySetException(ex);
                while (streamBag.TryTake(out TaskCompletionSource<QuicheStream>? tcs))
                {
                    tcs.TrySetException(ex);
                }
                throw;
            }
            catch (OperationCanceledException)
            {
                establishedTcs.TrySetCanceled(cts.Token);
                while (streamBag.TryTake(out TaskCompletionSource<QuicheStream>? tcs))
                {
                    tcs.TrySetCanceled(cts.Token);
                }
                throw;
            }
        }
    }

    private async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        byte[] packetBuf = new byte[QuicheLibrary.MAX_DATAGRAM_LEN];
        while (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ReadOnlyMemory<byte> nextPacket;
                if (!recvQueue.TryDequeue(out nextPacket))
                {
                    unsafe
                    {
                        lock (this)
                        {
                            NativePtr->OnTimeout();
                            if (NativePtr->IsEstablished())
                            {
                                establishedTcs.TrySetResult();
                            }
                        }
                    }

                    await Task.Delay(75, cancellationToken);
                    continue;
                }
                nextPacket.CopyTo(packetBuf);

                long resultOrError;
                bool isConnEstablished;
                unsafe
                {
                    lock (this)
                    {
                        var (to, to_len) = GetSocketAddress(socket.LocalEndPoint);
                        var (from, from_len) = GetSocketAddress(remoteEndPoint);

                        RecvInfo recvInfo = new RecvInfo
                        {
                            to = (sockaddr*)to.Pointer,
                            to_len = (size_t)to_len,

                            from = (sockaddr*)from.Pointer,
                            from_len = (size_t)from_len,
                        };

                        using (to)
                        using (from)
                        {
                            fixed (byte* bufPtr = packetBuf)
                            {
                                resultOrError = (long)NativePtr->Recv(
                                    bufPtr, (nuint)nextPacket.Length,
                                    (RecvInfo*)Unsafe.AsPointer(ref recvInfo)
                                    );
                                QuicheException.ThrowIfError((QuicheError)resultOrError, "An uncaught error occured in quiche!");
                            }
                        }

                        isConnEstablished = NativePtr->IsEstablished();
                    }
                }

                await Task.Yield();

                if (isConnEstablished && (establishedTcs.TrySetResult() || ConnectionEstablished.IsCompleted))
                {
                    long streamIdOrNone;
                    unsafe
                    {
                        lock (this)
                        {
                            streamIdOrNone = NativePtr->StreamReadableNext();
                        }
                    }

                    await Task.Yield();

                    if (streamIdOrNone >= 0)
                    {
                        long recvCount;
                        bool streamFinished = false;
                        unsafe
                        {
                            lock (this)
                            {
                                fixed (byte* bufPtr = packetBuf)
                                {
                                    recvCount = (long)NativePtr->StreamRecv((ulong)streamIdOrNone, bufPtr, (nuint)resultOrError,
                                        (bool*)Unsafe.AsPointer(ref streamFinished), (ulong*)Unsafe.AsPointer(ref resultOrError));
                                    QuicheException.ThrowIfError((QuicheError)resultOrError, "An uncaught error occured in quiche!");
                                }
                            }
                        }

                        QuicheStream stream = GetStream(streamIdOrNone, true);
                        if (!streamBag.TryTake(out TaskCompletionSource<QuicheStream>? tcs))
                        {
                            streamBag.Add(tcs = new());
                        }
                        tcs.TrySetResult(stream);

                        await stream.ReceiveDataAsync(
                            packetBuf.AsMemory(0, (int)recvCount),
                            streamFinished, cancellationToken
                            );
                    }
                }
            }
            catch (QuicheException ex) when
                (ex.ErrorCode == QuicheError.QUICHE_ERR_DONE)
            { await Task.Delay(75, cancellationToken); continue; }
            catch (QuicheException ex)
            {
                establishedTcs.TrySetException(ex);
                while (streamBag.TryTake(out TaskCompletionSource<QuicheStream>? tcs))
                {
                    tcs.TrySetException(ex);
                }
                throw;
            }
            catch (OperationCanceledException)
            {
                establishedTcs.TrySetCanceled(cts.Token);
                while (streamBag.TryTake(out TaskCompletionSource<QuicheStream>? tcs))
                {
                    tcs.TrySetCanceled(cts.Token);
                }
                throw;
            }
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] packetBuf = new byte[QuicheLibrary.MAX_DATAGRAM_LEN];
            SocketReceiveFromResult result = await socket.ReceiveFromAsync(
                packetBuf, remoteEndPoint, cancellationToken);
            recvQueue.Enqueue(packetBuf.AsMemory(0, result.ReceivedBytes));
        }
    }

    private QuicheStream GetStream(long streamId, bool isPeerInitiated) =>
        streamMap.GetOrAdd(streamId, id => new(this, id, isPeerInitiated));

    public QuicheStream GetStream() => 
        GetStream(streamMap.Count == 0 ? 
            0 : streamMap.Keys.Max() + 1, 
            false);

    public async Task<QuicheStream> AcceptInboundStreamAsync(CancellationToken cancellationToken)
    {
        if (!streamBag.TryTake(out TaskCompletionSource<QuicheStream>? tcs))
        {
            streamBag.Add(tcs = new());
        }
        return await tcs.Task.WaitAsync(cancellationToken);
    }

    private bool disposedValue;

    protected unsafe virtual void Dispose(bool disposing)
    {
        bool isNativeHandleValid;
        lock (this)
        {
            isNativeHandleValid = NativePtr is not null;
        }

        if (!disposedValue && isNativeHandleValid)
        {
            if (disposing)
            {
                try
                {
                    cts.Cancel();
                    Task.WhenAll(recvTask, sendTask,
                        listenTask ?? Task.CompletedTask
                        ).Wait();
                }
                catch (AggregateException ex)
                when (ex.InnerExceptions.All(
                    x => x is OperationCanceledException ||
                    x is QuicheException q && q.ErrorCode == QuicheError.QUICHE_ERR_DONE
                    ))
                { }

                foreach (var (_, stream) in streamMap)
                {
                    stream.Dispose();
                }

                try
                {
                    lock (this)
                    {
                        int errorResult;
                        byte[] reasonBuf = Encoding.UTF8.GetBytes("Connection was implicitly closed for user initiated disposal.");
                        fixed (byte* reasonPtr = reasonBuf)
                        {
                            errorResult = NativePtr->Close(true, 0x00, reasonPtr, (nuint)reasonBuf.Length);
                            QuicheException.ThrowIfError((QuicheError)errorResult, "Failed to close connection!");
                        }
                    }
                }
                finally
                {
                    cts.Dispose();

                    recvQueue.Clear();
                    sendQueue.Clear();

                    streamMap.Clear();
                    streamBag.Clear();

                    if (shouldCloseSocket) 
                    {
                        socket.Dispose();
                    }
                }
            }

            lock (this)
            {
                NativePtr->Free();
                NativePtr = null;
            }
        }

        disposedValue = true;
    }

    ~QuicheConnection()
    {
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
