using Authagonal.Core.Services;

namespace Authagonal.Server.Services;

/// <summary>
/// Default no-op implementation of <see cref="IAuthHook"/>.
/// </summary>
public sealed class NullAuthHook : IAuthHook
{
    public Task OnUserAuthenticatedAsync(string userId, string email, string method, string? clientId, CancellationToken ct) => Task.CompletedTask;
    public Task OnUserCreatedAsync(string userId, string email, string createdVia, CancellationToken ct) => Task.CompletedTask;
    public Task OnLoginFailedAsync(string email, string reason, CancellationToken ct) => Task.CompletedTask;
    public Task OnTokenIssuedAsync(string? subjectId, string clientId, string grantType, CancellationToken ct) => Task.CompletedTask;
}
