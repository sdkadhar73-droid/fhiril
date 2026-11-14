using System.Collections.Concurrent;

namespace FhirIl.Api.Authentication;

public sealed record SmartSandboxSession(string AccessToken, string PatientId, string Issuer, string Scope, DateTimeOffset ExpiresAt);

public interface ISmartSandboxSessionStore
{
    void Store(string id, SmartSandboxSession session);
    bool TryGet(string id, out SmartSandboxSession session);
    void Remove(string id);
}

public sealed class SmartSandboxSessionStore : ISmartSandboxSessionStore
{
    private readonly ConcurrentDictionary<string, SmartSandboxSession> _sessions = new(StringComparer.Ordinal);

    public void Store(string id, SmartSandboxSession session) => _sessions[id] = session;

    public bool TryGet(string id, out SmartSandboxSession session)
    {
        if (_sessions.TryGetValue(id, out session!))
        {
            if (session.ExpiresAt > DateTimeOffset.UtcNow) return true;
            _sessions.TryRemove(id, out _);
        }
        session = null!;
        return false;
    }

    public void Remove(string id) => _sessions.TryRemove(id, out _);
}
