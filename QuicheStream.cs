
using System.IO.Pipelines;

namespace Quiche.NET
{
    public class QuicheStream : Stream
    {
        public enum Shutdown
        {
            Read = 0,
            Write = 1,
        }

        private readonly Pipe? recvPipe, sendPipe;
        private readonly Stream? recvStream, sendStream;

        private readonly QuicheConnection conn;
        private readonly long streamId;

        private readonly ManualResetEventSlim readResetEvent;

        private bool isPeerInitiated;
        private bool flushCompleteFlag;

        public override bool CanRead => isPeerInitiated && !conn.IsClosed;

        public override bool CanWrite => !isPeerInitiated && !conn.IsClosed;

        public override bool CanSeek => false;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Length => throw new NotSupportedException();

        internal QuicheStream(QuicheConnection conn, long streamId, bool isPeerInitiated)
        {
            this.conn = conn;
            this.streamId = streamId;

            readResetEvent = new();

            if (isPeerInitiated)
            {
                recvPipe = new Pipe();
                recvStream = recvPipe.Reader.AsStream();
                this.isPeerInitiated = true;
            }
            else
            {
                sendPipe = new Pipe();
                sendStream = sendPipe.Writer.AsStream();
                this.isPeerInitiated = false;
            }
        }

        internal async Task ReceiveDataAsync(ReadOnlyMemory<byte> bufIn, bool finished, CancellationToken cancellationToken)
        {
            if (recvPipe is null || recvStream is null)
            {
                throw new NotSupportedException();
            }
            else
            {
                lock (recvPipe)
                {
                    Memory<byte> memory = recvPipe.Writer.GetMemory((int)QuicheLibrary.MAX_DATAGRAM_LEN);
                    bufIn.CopyTo(memory);
                    recvPipe.Writer.Advance(bufIn.Length);
                }

                FlushResult flushResult = await recvPipe.Writer.FlushAsync(cancellationToken);

                lock (recvPipe)
                {
                    flushCompleteFlag = flushResult.IsCompleted;
                    if (finished) { recvPipe.Writer.Complete(); }
                }

                readResetEvent.Set();
            }
        }

        public override void Flush()
        {
            sendStream?.Flush();

            while (sendPipe?.Reader.TryRead(out ReadResult result) ?? false)
            {
                foreach (var memory in result.Buffer)
                {
                    conn.sendQueue.Enqueue((streamId, memory.ToArray()));
                }
                sendPipe.Reader.AdvanceTo(result.Buffer.End);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (recvPipe is null || recvStream is null)
            {
                throw new NotSupportedException();
            }
            else
            {
                lock (recvPipe)
                {
                    int bytesRead = recvStream.Read(buffer, offset, count);
                    while (!flushCompleteFlag && bytesRead < count)
                    {
                        readResetEvent.Reset();
                        readResetEvent.Wait();
                        bytesRead += recvStream.Read(buffer, offset + bytesRead, count - bytesRead);
                    }

                    return bytesRead;
                }
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (sendStream is null)
            {
                throw new NotSupportedException();
            }
            else
            {
                sendStream.Write(buffer, offset, count);
                Flush();
            }
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public unsafe override void Close()
        {
            base.Close();

            recvPipe?.Writer.Complete();
            sendPipe?.Writer.Complete();

            try
            {
                if (CanRead)
                {
                    QuicheException.ThrowIfError((QuicheError)
                        conn.NativePtr->StreamShutdown((ulong)streamId,
                        (int)Shutdown.Read, 0x00),
                        $"Failed to shutdown reading side of stream! (ID: {streamId:X16})"
                        );
                }

                if (CanWrite)
                {
                    QuicheException.ThrowIfError((QuicheError)
                        conn.NativePtr->StreamShutdown((ulong)streamId,
                        (int)Shutdown.Write, 0x00),
                        $"Failed to shutdown writing side of stream! (ID: {streamId:X16})"
                        );
                }
            }
            catch (QuicheException ex)
            when (ex.ErrorCode == QuicheError.QUICHE_ERR_DONE)
            { }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                recvStream?.Dispose();
                sendStream?.Dispose();
            }
        }
    }
}
