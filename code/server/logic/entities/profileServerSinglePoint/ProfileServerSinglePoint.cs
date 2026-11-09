using TGame.Server;

namespace TGame.Server.Logic;

[SinglePointFor(ProcessType.Profile)]
internal sealed class ProfileServerSinglePoint : ServerSinglePoint, IDisposable
{
    public ProfileStore Store { get; private set; } = null!;

    protected override async Task InitializeRoleAsync(CancellationToken stop) =>
        Store = await ProfileStore.OpenAsync(stop);

    public void Dispose() => Store?.Dispose();
}
