using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using YakUI.Core.Platform;

namespace YakUI.Platform.Windows;

/// <summary>Проверка Authenticode через WinVerifyTrust и чтение издателя (FR-17).</summary>
public sealed partial class WindowsSignatureVerifier : ISignatureVerifier
{
    private const uint TrustENoSignature = 0x800B0100;
    private const uint TrustESubjectFormUnknown = 0x800B0003;
    private const uint TrustEProviderUnknown = 0x800B0001;

    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public SignatureInfo Verify(string filePath)
    {
        var result = WinVerify(filePath);
        if (result is TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown)
            return new SignatureInfo(SignatureState.Unsigned, null);

        return new SignatureInfo(result == 0 ? SignatureState.Valid : SignatureState.Invalid, ReadPublisher(filePath));
    }

    private static string? ReadPublisher(string filePath)
    {
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile — штатный способ получить сертификат подписи Authenticode.
            using var cert = X509Certificate.CreateFromSignedFile(filePath);
            using var cert2 = new X509Certificate2(cert);
#pragma warning restore SYSLIB0057
            return cert2.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static uint WinVerify(string filePath)
    {
        var filePathPtr = Marshal.StringToCoTaskMemUni(filePath);
        var fileInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WintrustFileInfo>());
        var dataPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WintrustData>());
        try
        {
            Marshal.StructureToPtr(new WintrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
                pcwszFilePath = filePathPtr,
            }, fileInfoPtr, false);

            Marshal.StructureToPtr(new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = fileInfoPtr,
                dwStateAction = 0,         // WTD_STATEACTION_IGNORE
                dwProvFlags = 0x00000080,  // WTD_REVOCATION_CHECK_NONE — без сетевых запросов
            }, dataPtr, false);

            var action = WintrustActionGenericVerifyV2;
            return unchecked((uint)WinVerifyTrust(IntPtr.Zero, ref action, dataPtr));
        }
        finally
        {
            Marshal.FreeCoTaskMem(filePathPtr);
            Marshal.FreeCoTaskMem(fileInfoPtr);
            Marshal.FreeCoTaskMem(dataPtr);
        }
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
