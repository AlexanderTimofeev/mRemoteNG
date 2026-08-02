using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace mRemoteNG.Connection.Protocol.RDP
{
    public sealed class WindowsCredentialManager
    {
        private const uint CredTypeGeneric = 1;
        private const uint CredPersistLocalMachine = 2;

        public void Write(string targetName, string username, string password)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
            ArgumentException.ThrowIfNullOrWhiteSpace(username);
            ArgumentNullException.ThrowIfNull(password);

            IntPtr targetPtr = IntPtr.Zero;
            IntPtr usernamePtr = IntPtr.Zero;
            IntPtr passwordPtr = IntPtr.Zero;
            try
            {
                targetPtr = Marshal.StringToCoTaskMemUni(targetName);
                usernamePtr = Marshal.StringToCoTaskMemUni(username);
                passwordPtr = Marshal.StringToCoTaskMemUni(password);

                Credential credential = new()
                {
                    Type = CredTypeGeneric,
                    TargetName = targetPtr,
                    UserName = usernamePtr,
                    CredentialBlob = passwordPtr,
                    CredentialBlobSize = checked((uint)Encoding.Unicode.GetByteCount(password)),
                    Persist = CredPersistLocalMachine
                };

                if (!CredWriteW(ref credential, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Unable to store Windows credential '{targetName}'.");
            }
            finally
            {
                if (passwordPtr != IntPtr.Zero)
                    Marshal.ZeroFreeCoTaskMemUnicode(passwordPtr);
                if (usernamePtr != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(usernamePtr);
                if (targetPtr != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(targetPtr);
            }
        }

        public void Delete(string targetName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
            if (!CredDeleteW(targetName, CredTypeGeneric, 0))
            {
                int error = Marshal.GetLastWin32Error();
                const int ErrorNotFound = 1168;
                if (error != ErrorNotFound)
                    throw new Win32Exception(error, $"Unable to delete Windows credential '{targetName}'.");
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public uint Flags;
            public uint Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredWriteW(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredDeleteW(string target, uint type, uint flags);
    }
}
