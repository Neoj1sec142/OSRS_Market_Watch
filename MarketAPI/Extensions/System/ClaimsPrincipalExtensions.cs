using System.Security.Claims;
using MarketAPI.Options;

namespace MarketAPI.Extensions.System;



public static class ClaimsPrincipalExtensions
{
    public static string? GetUsername(this ClaimsPrincipal user)
    {
        return user.FindFirstValue(ClaimTypes.Name);
    }

    public static int? GetUserId(this ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(value, out var id) ? id : null;
    }

    // public static int? GetRoleId(this ClaimsPrincipal user)
    // {
    //     var value = user.FindFirstValue(AppClaimTypes.RoleId);
    //     return int.TryParse(value, out var id) ? id : null;
    // }

    public static string? GetRoleName(this ClaimsPrincipal user)
    {
        return user.FindFirstValue(ClaimTypes.Role);
    }
}