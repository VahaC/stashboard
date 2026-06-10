using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Stashboard.Api.Controllers;

namespace Stashboard.Tests.Controllers;

/// <summary>
/// Guards against a whole class of routing bug that controller unit tests can't
/// catch (they invoke the action method directly, bypassing routing): using a
/// <strong>reserved</strong> token as a route parameter. In attribute routing
/// <c>{action}</c> / <c>{controller}</c> / <c>{area}</c> / <c>{page}</c> /
/// <c>{handler}</c> are special — e.g. <c>{action}</c> binds to the action
/// <em>method name</em>, so a route like <c>.../status/{action}</c> never matches
/// a real URL value and silently falls through to the SPA fallback as a 405. This
/// is exactly the bug that shipped on the LXC lifecycle route until it was renamed
/// to <c>{verb}</c>.
/// </summary>
public class RouteTemplateConventionsTests
{
    private static readonly string[] ReservedTokens = ["action", "controller", "area", "page", "handler"];

    // Matches a route token whose name is reserved, with or without a constraint:
    // {action}, {action:int}, { action }, etc.
    private static readonly Regex ReservedTokenRegex = new(
        @"\{\s*(action|controller|area|page|handler)\s*(:|\})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void NoControllerRouteTemplate_UsesAReservedRoutingToken()
    {
        var offenders = new List<string>();

        var controllers = typeof(ProxmoxConnectionsController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);

        foreach (var controller in controllers)
        {
            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var attr in method.GetCustomAttributes().OfType<IRouteTemplateProvider>())
                {
                    var template = attr.Template;
                    if (!string.IsNullOrEmpty(template) && ReservedTokenRegex.IsMatch(template))
                        offenders.Add($"{controller.Name}.{method.Name}: \"{template}\"");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Route templates must not use a reserved routing token "
            + $"({string.Join(", ", ReservedTokens.Select(t => $"{{{t}}}"))}) as a parameter — "
            + "such routes never match real URLs and fall through as 405. Offenders:\n"
            + string.Join("\n", offenders));
    }
}
