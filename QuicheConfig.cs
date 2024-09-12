using System.Runtime.InteropServices;
using System.Text;
using static Quiche.NativeMethods;

namespace Quiche.NET
{
    public unsafe class QuicheConfig : IDisposable
    {
        // quiche_config handle

        private Config* nativePtr;

        // quiche_config properties

        private bool isActiveMigrationEnabled;

        private readonly bool isEarlyDataEnabled;

        private bool isHyStartEnabled;

        private bool isPacingEnabled;

        private bool shouldDiscoverPathMtu;

        private readonly bool shouldLogKeys;

        private bool shouldSendGrease;

        private bool shouldVerifyPeer;

        public bool IsActiveMigrationEnabled
        {
            get
            {
                return isActiveMigrationEnabled;
            }
            set
            {
                nativePtr->SetDisableActiveMigration(isActiveMigrationEnabled = !value);
            }
        }

        public bool IsEarlyDataEnabled
        {
            get
            {
                return isEarlyDataEnabled;
            }
        }

        public bool IsHyStartEnabled
        {
            get
            {
                return isHyStartEnabled;
            }
            set
            {
                nativePtr->EnableHystart(isHyStartEnabled = value);
            }
        }

        public bool IsPacingEnabled
        {
            get
            {
                return isPacingEnabled;
            }
            set
            {
                nativePtr->EnablePacing(isPacingEnabled = value);
            }
        }

        public bool ShouldDiscoverPathMtu
        {
            get
            {
                return shouldDiscoverPathMtu;
            }
            set
            {
                nativePtr->DiscoverPmtu(shouldDiscoverPathMtu = value);
            }
        }

        public bool ShouldLogKeys
        {
            get
            {
                return shouldLogKeys;
            }
        }

        public bool ShouldSendGrease
        {
            get
            {
                return shouldSendGrease;
            }
            set
            {
                nativePtr->VerifyPeer(shouldSendGrease = value);
            }
        }

        public bool ShouldVerifyPeer
        {
            get
            {
                return shouldVerifyPeer;
            }
            set
            {
                nativePtr->VerifyPeer(shouldVerifyPeer = value);
            }
        }

        public QuicheConfig(
            bool isEarlyDataEnabled,
            bool shouldLogKeys
            )
        {
            nativePtr = quiche_config_new(QuicheLibrary.PROTOCOL_VERSION);

            if (shouldLogKeys)
            {
                nativePtr->LogKeys();
                this.shouldLogKeys = true;
            }

            if (isEarlyDataEnabled)
            {
                nativePtr->EnableEarlyData();
                this.isEarlyDataEnabled = true;
            }
        }

        public void LoadCertificateChainFromPemFile(string filePath)
        {
            fixed (byte* filePathPtr = Encoding.Default.GetBytes(filePath)) 
            {
                QuicheException.ThrowIfError(
                    (QuicheError)nativePtr->LoadCertChainFromPemFile(filePathPtr),
                    "Failed to load certificate chain from provided PEM file!"
                    );
            }
        }

        #region IDisposable

        private bool disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                nativePtr->Free();
                nativePtr = null;

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
