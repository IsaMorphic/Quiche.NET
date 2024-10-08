
using System.Buffers;
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
        private readonly ulong streamId;

        private bool firstReadFlag, firstWriteFlag;

        public override bool CanRead => !(firstReadFlag && conn.IsStreamFinished(streamId));

        public override bool CanWrite => !(firstWriteFlag && conn.IsStreamFinished(streamId));

        public override bool CanSeek => false;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Length => throw new NotSupportedException();

        internal QuicheStream(QuicheConnection conn, ulong streamId)
        {
            this.conn = conn;
            this.streamId = streamId;

            recvPipe = new Pipe();
            recvStream = recvPipe.Reader.AsStream(leaveOpen: true);

            sendPipe = new Pipe();
            sendStream = sendPipe.Writer.AsStream(leaveOpen: true);
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

                await recvPipe.Writer.FlushAsync(cancellationToken);

                if (finished)
                {
                    recvPipe.Writer.Complete();
                }
            }
        }

        public override void Flush()
        {
            sendStream?.Flush();

            if (sendPipe?.Reader.TryRead(out ReadResult result) ?? false)
            {
                conn.sendQueue.Enqueue((streamId, result.Buffer.ToArray()));
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
                    int readCount = recvStream.Read(buffer, offset, count);
                    firstReadFlag = true;
                    return readCount;
                }
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (sendPipe is null || sendStream is null)
            {
                throw new NotSupportedException();
            }
            else
            {
                sendStream.Write(buffer, offset, count);
                firstWriteFlag = true;
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
                        conn.NativePtr->StreamShutdown(streamId,
                        (int)Shutdown.Read, 0x00),
                        $"Failed to shutdown reading side of stream! (ID: {streamId:X16})"
                        );
                }

                if (CanWrite)
                {
                    QuicheException.ThrowIfError((QuicheError)
                        conn.NativePtr->StreamShutdown(streamId,
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
