using System.Security.Cryptography;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum MachineIntegrationSharedKeyStatus
{
    Missing,
    SessionReady,
    SessionTooShort,
    SessionMalformed,
    EnvironmentReady,
    EnvironmentTooShort,
    EnvironmentMalformed
}

/// <summary>
/// Owns the transient shared-key bytes and environment fallback. It has no
/// WPF or ViewModel dependency and never persists the key.
/// </summary>
internal sealed class MachineIntegrationSharedKeyStore : IDisposable
{
    internal const string EnvironmentVariableName = "OPENVISIONLAB_TCP_SHARED_KEY";

    private readonly Func<string?> _environmentValueProvider;
    private byte[]? _sessionKey;
    private MachineIntegrationSharedKeyStatus? _sessionStatus;
    private bool _disposed;

    public MachineIntegrationSharedKeyStore(Func<string?>? environmentValueProvider = null) =>
        _environmentValueProvider = environmentValueProvider ?? ReadEnvironmentValue;

    public MachineIntegrationSharedKeyStatus Status
    {
        get
        {
            ThrowIfDisposed();
            return _sessionStatus ?? GetEnvironmentStatus();
        }
    }

    public MachineIntegrationSharedKeyStatus SetSessionKey(string? encodedKey)
    {
        ThrowIfDisposed();
        ClearSessionKey();
        _sessionStatus = null;
        if (string.IsNullOrWhiteSpace(encodedKey))
        {
            return Status;
        }

        _sessionStatus = Decode(encodedKey, session: true, out _sessionKey);
        return _sessionStatus.Value;
    }

    public byte[]? TryAcquire()
    {
        ThrowIfDisposed();
        if (_sessionStatus is { } sessionStatus)
        {
            return sessionStatus == MachineIntegrationSharedKeyStatus.SessionReady
                ? _sessionKey?.ToArray()
                : null;
        }

        var status = Decode(_environmentValueProvider(), session: false, out var key);
        if (status == MachineIntegrationSharedKeyStatus.EnvironmentReady)
        {
            return key;
        }

        if (key is not null)
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearSessionKey();
        _sessionStatus = null;
    }

    private MachineIntegrationSharedKeyStatus GetEnvironmentStatus()
    {
        var status = Decode(_environmentValueProvider(), session: false, out var key);
        if (key is not null)
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return status;
    }

    private void ClearSessionKey()
    {
        if (_sessionKey is not null)
        {
            CryptographicOperations.ZeroMemory(_sessionKey);
            _sessionKey = null;
        }
    }

    private static MachineIntegrationSharedKeyStatus Decode(
        string? encodedKey,
        bool session,
        out byte[]? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(encodedKey))
        {
            return session
                ? MachineIntegrationSharedKeyStatus.SessionMalformed
                : MachineIntegrationSharedKeyStatus.Missing;
        }

        try
        {
            var parsed = Convert.FromBase64String(encodedKey.Trim());
            if (parsed.Length < 32)
            {
                CryptographicOperations.ZeroMemory(parsed);
                return session
                    ? MachineIntegrationSharedKeyStatus.SessionTooShort
                    : MachineIntegrationSharedKeyStatus.EnvironmentTooShort;
            }

            key = parsed;
            return session
                ? MachineIntegrationSharedKeyStatus.SessionReady
                : MachineIntegrationSharedKeyStatus.EnvironmentReady;
        }
        catch (FormatException)
        {
            return session
                ? MachineIntegrationSharedKeyStatus.SessionMalformed
                : MachineIntegrationSharedKeyStatus.EnvironmentMalformed;
        }
    }

    private static string? ReadEnvironmentValue() =>
        Environment.GetEnvironmentVariable(EnvironmentVariableName);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
