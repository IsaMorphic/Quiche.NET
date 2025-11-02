using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Quiche.NET;

public class QuicheListener : IDisposable
{
    private readonly ConcurrentDictionary<EndPoint, QuicheConnection> connMap;
    private readonly Channel<QuicheConnection> connChannel;

    private readonly Socket socket;
    private readonly QuicheConfig config;

    public QuicheListener(Socket socket, QuicheConfig config)
    {
        this.socket = socket;
        this.config = config;

        connMap = new();
        connChannel = Channel.CreateUnbounded<QuicheConnection>();
    }

    public async Task ListenAsync(CancellationToken cancellationToken)
    {
        byte[] recvBuffer = new byte[QuicheLibrary.MAX_BUFFER_LEN];
        while (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SocketReceiveFromResult recvResult = await socket.ReceiveFromAsync(recvBuffer, new IPEndPoint(IPAddress.None, 0), cancellationToken);
            ReadOnlyMemory<byte> receivedBytes = ((byte[])recvBuffer.Clone())
                    .AsMemory(0, recvResult.ReceivedBytes);

            if (connMap.TryGetValue(recvResult.RemoteEndPoint, out QuicheConnection? connection))
            {
                connection.recvQueue.Enqueue(receivedBytes);
            }
            else
            {
                QuicheConnection conn = QuicheConnection.Accept(socket, recvResult.RemoteEndPoint, receivedBytes, config);
                connMap.TryAdd(recvResult.RemoteEndPoint, conn);

                await connChannel.Writer.WriteAsync(conn, cancellationToken);
            }
        }
    }

    public async Task<QuicheConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        return await connChannel.Reader.ReadAsync(cancellationToken);
    }


    private bool disposedValue;

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                connMap.Clear();
            }

            disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
