using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;

namespace NksTaskMgr.WinUI;

internal sealed class ElevationBrokerTrustException(uint error, string message) : IOException(message)
{
    internal uint Error { get; } = error;
}

internal sealed class ElevationBrokerTrust : IDisposable
{
    private readonly SignedImageLease image;
    private ElevationBrokerTrust(SignedImageLease image) => this.image = image;
    internal string Path => image.Path;

    internal static ElevationBrokerTrust Open()
    {
        byte[]? pin = PublisherTrust.ReadPin(PublisherPolicy.Broker);
        if (pin is null) throw new ElevationBrokerTrustException(0xe04e0001, ElevationBrokerClient.Status(0xe04e0001));
        try { return new(SignedImageLease.Open(System.IO.Path.Combine(AppContext.BaseDirectory, "nks-elevation-broker.exe"), pin)); }
        catch (Exception error) when (error is IOException or Win32Exception or CryptographicException) {
            throw new ElevationBrokerTrustException(0xe04e0002, "Broker nebyl spuštěn: " + error.Message);
        }
    }
    internal void VerifyProcess(Process process)
    {
        try { image.VerifyProcess(process); }
        catch (IOException error) { throw new ElevationBrokerTrustException(0xe04e0002, error.Message); }
    }
    public void Dispose() => image.Dispose();
}