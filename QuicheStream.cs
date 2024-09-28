
using System.IO.Pipelines;


namespace Quiche.NET
{
    public class QuicheStream : Stream
    {
        private readonly Pipe recvPipe;
        private readonly Stream recvStream;

        private long streamId;
        private readonly QuicheConnection connection;

        public override bool CanRead => throw new NotImplementedException();

        public override bool CanSeek => throw new NotImplementedException();

        public override bool CanWrite => throw new NotImplementedException();

        public override long Length => throw new NotImplementedException();

        public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        internal QuicheStream(QuicheConnection connection, long streamId)
        {
            recvPipe = new Pipe();
            recvStream = recvPipe.Reader.AsStream(true);

            this.connection = connection;
            this.streamId = streamId;
        }

        internal async Task<bool> BufferDataReceiveAsync(byte[] buffer, bool finished)
        {
            lock (recvPipe.Writer)
            {
                const int minimumBufferSize = 512;

                Memory<byte> memory = recvPipe.Writer.GetMemory(minimumBufferSize);

                buffer.CopyTo(memory.Span);
                recvPipe.Writer.Advance(buffer.Length);
            }

            // Make the data available to the PipeReader.
            FlushResult result = await recvPipe.Writer.FlushAsync();

            if (finished)
            {
                await recvPipe.Writer.CompleteAsync();
            }

            return result.IsCompleted;
        }

        public override void Flush()
        {
            throw new NotImplementedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            recvStream.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }
    }
}
