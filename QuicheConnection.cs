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
            byte[] recvBuff = ArrayPool<byte>.Shared.Rent(512);
            EndPoint remoteEndPoint = new IPEndPoint(IPAddress.None, 0);
            int readCount = dgramSocket.ReceiveFrom(recvBuff, ref remoteEndPoint);

            RecvInfo recvInfo = new RecvInfo
            {
                to = GetSocketAddress(dgramSocket.LocalEndPoint),
                to_len = sizeof(sockaddr_storage),

                from = GetSocketAddress(remoteEndPoint),
                from_len = sizeof(sockaddr_storage),
            };

            long streamId;
            nint recvCount;
            fixed (byte* recvBuffPtr = recvBuff)
            {
                recvCount = quiche_conn_recv(nativePtr, recvBuffPtr, 
                    (nuint)readCount, (RecvInfo*)Unsafe.AsPointer(ref recvInfo));
                
                streamId = quiche_conn_stream_readable_next(nativePtr);
                if(streamId < 0) 
                {
                    SendInfo sendInfo = default;
                    nint sendCount = quiche_conn_send(nativePtr, recvBuffPtr, (nuint)recvBuff.Length, 
                        (SendInfo*)Unsafe.AsPointer(ref sendInfo));
                    dgramSocket.SendTo(new ReadOnlySpan<byte>(recvBuffPtr, (int)sendCount), new IPEndPoint(
                        new IPAddress(new ReadOnlySpan<byte>(sendInfo.to.data, sendInfo.to_len)), 0));
                    continue;
                }

                bool streamFinished = false;
                QuicheError errorCode = QuicheError.QUICHE_ERR_NONE;
                recvCount = quiche_conn_stream_recv(nativePtr, (ulong)streamId, recvBuffPtr, (nuint)recvCount, 
                    (bool*)Unsafe.AsPointer(ref streamFinished), (ulong*)Unsafe.AsPointer(ref errorCode));
                QuicheException.ThrowIfError(errorCode, "An unexpected error occured in quiche!");

                // TODO: System.IO.Pipelines code for QuicheStream instance, passing recvCount and recvBuffer as input           
            }
        }
    }

    private static sockaddr* GetSocketAddress(EndPoint? endPoint)
    {
        sockaddr_storage sockAddr;
        IPEndPoint? ipEndPoint = endPoint as IPEndPoint;
        sockAddr.ss_family = (int)(ipEndPoint?.AddressFamily ?? AddressFamily.Unknown);
        (ipEndPoint?.Address.GetAddressBytes() ?? [])
        .CopyTo(new Span<byte>(sockAddr.data, 64));

        return (sockaddr*)Unsafe.AsPointer(ref sockAddr);
    }

    public static QuicheConnection Accept(QuicheConfig config)
    {
        Socket localSocket = new Socket(SocketType.Dgram, ProtocolType.Udp);
        Socket remoteSocket = localSocket.Accept();

        return new(quiche_accept(null, 0, null, 0,
            GetSocketAddress(localSocket.LocalEndPoint), sizeof(sockaddr_storage), 
            GetSocketAddress(remoteSocket.RemoteEndPoint), sizeof(sockaddr_storage),
            config.NativePtr), localSocket);
    }
}
