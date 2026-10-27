using Microsoft.AspNetCore.Identity;

namespace OrderFlow.Api;

public sealed class RoleSeeder(IServiceScopeFactory scopeFactory) : IHostedService
{
    private static readonly string[] RoleNames = ["Customer", "Support", "InventoryManager", "Admin"];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var name in RoleNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await roles.RoleExistsAsync(name)) continue;
            var result = await roles.CreateAsync(new IdentityRole<Guid>(name));
            if (!result.Succeeded && !await roles.RoleExistsAsync(name))
                throw new InvalidOperationException($"Could not seed required role '{name}'.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
