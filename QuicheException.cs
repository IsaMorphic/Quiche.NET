namespace Quiche.NET
{
    public class QuicheException : Exception
    {
        private const string DEFAULT_MESSAGE = "An uncaught exception was raised by quiche!";

        public QuicheError ErrorCode { get; }

        internal QuicheException(QuicheError errorCode, string? message) 
            : base($"{message ?? DEFAULT_MESSAGE}\nCode: {errorCode}")
        {
            ErrorCode = errorCode;
            HResult = (int)errorCode;
        }

        public static void ThrowIfError(QuicheError errorCode, string? message = null)
        {
            if (errorCode >= 0) 
            {
                return;
            }

            throw new QuicheException(errorCode, message);
        }
    }
}
