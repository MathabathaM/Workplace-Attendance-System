using System.Net;
using System.Security.Claims;
using Workplace.Data.Auth;

namespace Workplace.Api.Services;

public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? UserId =>
        Guid.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public Guid? EmployeeId =>
        Guid.TryParse(Principal?.FindFirstValue(AppClaimsPrincipalFactory.EmployeeIdClaim), out var id) ? id : null;

    public IReadOnlyCollection<string> Roles =>
        Principal?.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray() ?? [];

    public IPAddress? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress;
}
