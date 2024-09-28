using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using static Quiche.NativeMethods;

namespace Quiche.NET;

public unsafe class QuicheConnection
{
    private Conn* nativePtr;

    private Socket dgramSocket;

    private readonly ConcurrentQueue<byte[]> sendQueue;


    private QuicheConnection(Conn* nativePtr, Socket dgramSocket)
    {
        this.nativePtr = nativePtr;
        this.dgramSocket = dgramSocket;

        sendQueue = new();
    }

    private void RunDriver()
    {
        for (; ; )
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(512);
            EndPoint remoteEndPoint = new IPEndPoint(IPAddress.None, 0);
            int readCount = dgramSocket.ReceiveFrom(buffer, ref remoteEndPoint);

            var (to, to_len) = GetSocketAddress(dgramSocket.LocalEndPoint);
            var (from, from_len) = GetSocketAddress(remoteEndPoint);

            RecvInfo recvInfo = new RecvInfo
            {
                to = (sockaddr*)to.Pointer,
                to_len = to_len,

                from = (sockaddr*)from.Pointer,
                from_len = from_len,
            };

            long streamId;
            long recvCount;
            fixed (byte* bufPtr = buffer)
            {
                using (to)
                using (from)
                {
                    recvCount = (long)quiche_conn_recv(nativePtr, bufPtr,
                        (nuint)readCount, (RecvInfo*)Unsafe.AsPointer(ref recvInfo));
                }

                bool flag;
                streamId = quiche_conn_stream_readable_next(nativePtr);
                if ((flag = streamId < 0) && (streamId = quiche_conn_stream_writable_next(nativePtr)) < 0)
                {
                    SendInfo sendInfo = default;
                    long sendCount = (long)quiche_conn_send(nativePtr, bufPtr, (nuint)buffer.Length,
                        (SendInfo*)Unsafe.AsPointer(ref sendInfo));

                    SocketAddress sendAddr = new((AddressFamily)sendInfo.to.ss_family, sendInfo.to_len);
                    Span<byte> sendAddrSpan = new Span<byte>((byte*)Unsafe.AsPointer(ref sendInfo.to), sendInfo.to_len);
                    sendAddrSpan.CopyTo(sendAddr.Buffer.Span);

                    ReadOnlySpan<byte> sendBuf = new(bufPtr, (int)sendCount);
                    dgramSocket.SendTo(sendBuf, SocketFlags.None, sendAddr);
                    continue;
                }
                else if (flag)
                {
                    bool streamFinished = false;
                    long errorCode = (long)QuicheError.QUICHE_ERR_NONE;
                    recvCount = quiche_conn_stream_recv(nativePtr, (ulong)streamId, bufPtr, (nuint)recvCount,
                        (bool*)Unsafe.AsPointer(ref streamFinished), (ulong*)Unsafe.AsPointer(ref errorCode));
                    QuicheException.ThrowIfError((QuicheError)errorCode, "An unexpected error occured in quiche!");

                    // TODO: System.IO.Pipelines code for QuicheStream instance, passing recvCount and buffer as input           
                }
            }
        }
    }

    private static (MemoryHandle, int) GetSocketAddress(EndPoint? endPoint)
    {
        Memory<byte>? buf = endPoint?.Serialize().Buffer;
        return buf is null ? default : (buf.Value.Pin(), buf.Value.Length);
    }

    public static QuicheConnection Accept(QuicheConfig config)
    {
        Socket localSocket = new Socket(SocketType.Dgram, ProtocolType.Udp);
        Socket remoteSocket = localSocket.Accept();

        var (local, local_len) = GetSocketAddress(localSocket.LocalEndPoint);
        var (remote, remote_len) = GetSocketAddress(remoteSocket.RemoteEndPoint);

        using (local)
        using (remote)
        {
            return new(quiche_accept(null, 0, null, 0, 
                (sockaddr*)local.Pointer, local_len, 
                (sockaddr*)remote.Pointer, remote_len, 
                config.NativePtr), localSocket);
        }
    }
}
