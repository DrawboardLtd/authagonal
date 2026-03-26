namespace Authagonal.Core.Services;

public interface IUserProvisioningService
{
    Task<ProvisioningResult> ProvisionUserAsync(ProvisioningRequest request, CancellationToken ct = default);
    Task NotifyUserDeletedAsync(string userId, string email, CancellationToken ct = default);
}

public sealed record ProvisioningRequest
{
    public required string Email { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Provider { get; init; }
    public string? ProviderKey { get; init; }
    public string? OrganizationId { get; init; }
}

public sealed record ProvisioningResult
{
    public required bool Approved { get; init; }
    public string? UserId { get; init; }
    public string? OrganizationId { get; init; }
    public string? Reason { get; init; }
}
