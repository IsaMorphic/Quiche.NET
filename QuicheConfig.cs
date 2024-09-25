using System.Text;
using static Quiche.NativeMethods;
using static Quiche.NET.QuicheLibrary;

namespace Quiche.NET
{
    public unsafe class QuicheConfig : IDisposable
    {
        // quiche_config handle

        internal Config* NativePtr { get; private set; }

        // quiche_config properties

        public bool IsActiveMigrationDisabled
        {
            set
            {
                NativePtr->SetDisableActiveMigration(value);
            }
        }

        public bool IsHyStartEnabled
        {
            set
            {
                NativePtr->EnableHystart(value);
            }
        }

        public bool IsPacingEnabled
        {
            set
            {
                NativePtr->EnablePacing(value);
            }
        }

        public bool ShouldDiscoverPathMtu
        {
            set
            {
                NativePtr->DiscoverPmtu(value);
            }
        }

        public bool ShouldSendGrease
        {
            set
            {
                NativePtr->VerifyPeer(value);
            }
        }

        public bool ShouldVerifyPeer
        {
            set
            {
                NativePtr->VerifyPeer(value);
            }
        }

        public QuicheConfig(
            bool isEarlyDataEnabled = false,
            bool shouldLogKeys = false
            )
        {
            NativePtr = quiche_config_new(PROTOCOL_VERSION);

            if (isEarlyDataEnabled)
            {
                NativePtr->EnableEarlyData();
            }

            if (shouldLogKeys)
            {
                NativePtr->LogKeys();
            }
        }

        public void LoadCertificateChainFromPemFile(string filePath)
        {
            fixed (byte* filePathPtr = Encoding.Default.GetBytes(filePath))
            {
                QuicheException.ThrowIfError(
                    (QuicheError)NativePtr->LoadCertChainFromPemFile(filePathPtr),
                    "Failed to load certificate chain from provided PEM file!"
                    );
            }
        }

        public void LoadPrivateKeyFromPemFile(string filePath)
        {
            fixed (byte* filePathPtr = Encoding.Default.GetBytes(filePath))
            {
                QuicheException.ThrowIfError(
                    (QuicheError)NativePtr->LoadPrivKeyFromPemFile(filePathPtr),
                    "Failed to load private key from provided PEM file!"
                    );
            }
        }

        public void LoadVerifyLocationsFromFile(string filePath)
        {
            fixed (byte* filePathPtr = Encoding.Default.GetBytes(filePath))
            {
                QuicheException.ThrowIfError(
                    (QuicheError)NativePtr->LoadVerifyLocationsFromFile(filePathPtr),
                    "Failed to load trusted CA locations from provided file!"
                    );
            }
        }

        public void LoadVerifyLocationsFromDirectory(string path)
        {
            fixed (byte* pathPtr = Encoding.Default.GetBytes(path))
            {
                QuicheException.ThrowIfError(
                    (QuicheError)NativePtr->LoadVerifyLocationsFromDirectory(pathPtr),
                    "Failed to load trusted CA locations from provided directory!"
                    );
            }
        }

        public void SetApplicationProtocols(params string[] protos)
        {
            List<byte> protoList = new();
            foreach (string proto in protos)
            {
                protoList.AddRange([..Encoding.Default.GetBytes(proto), 0]);
            }

            fixed (byte* protosPtr = protoList.ToArray())
            {
                NativePtr->SetApplicationProtos(protosPtr, (nuint)protoList.Count);
            }
        }

        #region IDisposable

        private bool disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                NativePtr->Free();
                NativePtr = null;

                disposedValue = true;
            }
        }

        ~QuicheConfig()
        {
            Dispose(disposing: false);
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
