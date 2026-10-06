using TypePilot.Core;

namespace TypePilot.App;

public interface IRewriteRuntime : IDisposable
{
    event Action<string>? StatusChanged;
    long MemoryMb { get; }
    Task<string> RewriteAsync(PilotSettings settings, string text, RewriteStyle style, CancellationToken token);
    Task UnloadAsync();
}
