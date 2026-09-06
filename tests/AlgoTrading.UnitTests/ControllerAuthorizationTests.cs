using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Guards the authorization attributes on the API's controllers.
/// </summary>
/// <remarks>
/// Two of these were found commented out, on endpoints whose own documentation
/// called them admin-only: the broker history backfill, and an instrument
/// import that reads an arbitrary path on the API host. A commented-out
/// attribute is worse than a missing one — it reads as protection to anyone
/// skimming the file, and to every reviewer after them.
/// <para>
/// Source is scanned rather than reflected over, because the failure being
/// guarded against is a line of source that looks right and does nothing.
/// </para>
/// </remarks>
public class ControllerAuthorizationTests
{
    private static DirectoryInfo ControllersDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var controllers = new DirectoryInfo(
            Path.Combine(dir!.FullName, "src", "AlgoTrading.Api", "Controllers"));
        Assert.True(controllers.Exists, $"controllers not found under {dir.FullName}");
        return controllers;
    }

    private static IEnumerable<FileInfo> ControllerFiles() =>
        ControllersDirectory().GetFiles("*Controller.cs");

    [Fact]
    public void No_authorization_attribute_is_left_commented_out()
    {
        var offenders = new List<string>();

        foreach (var file in ControllerFiles())
        {
            foreach (var (line, i) in File.ReadLines(file.FullName).Select((l, i) => (l, i)))
            {
                string trimmed = line.TrimStart();
                if (!trimmed.StartsWith("//", StringComparison.Ordinal)) continue;

                if (trimmed.Contains("[Authorize", StringComparison.Ordinal) ||
                    trimmed.Contains("[RequireModule", StringComparison.Ordinal))
                {
                    offenders.Add($"{file.Name}:{i + 1} {trimmed}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "authorization commented out:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void An_endpoint_documented_as_admin_only_is_actually_restricted()
    {
        // The instrument import said "Admin-only" in its own summary and
        // carried no attribute at all.
        var offenders = new List<string>();

        foreach (var file in ControllerFiles())
        {
            string body = File.ReadAllText(file.FullName);
            bool classAdmin = Regex.IsMatch(body,
                @"^\s*\[Authorize\(Policy = AuthorizationPolicies\.AdminOnly\)\]\s*$\s*(\[[^\]]+\]\s*)*\s*(\[ApiController\]|\[Route)",
                RegexOptions.Multiline);

            foreach (Match m in Regex.Matches(body,
                @"///[^\r\n]*?Admin-only(?<between>.*?)\[Http(?<verb>Get|Post|Put|Patch|Delete)",
                RegexOptions.Singleline))
            {
                // Only the attributes between the doc comment and the verb count.
                string between = m.Groups["between"].Value;
                if (classAdmin || between.Contains("AdminOnly", StringComparison.Ordinal)) continue;
                offenders.Add($"{file.Name}: an endpoint documented Admin-only has no AdminOnly policy");
            }
        }

        Assert.True(offenders.Count == 0, string.Join("\n  ", offenders));
    }

    [Fact]
    public void Every_controller_is_covered_by_authentication_or_says_why_not()
    {
        // The fallback policy authenticates everything, so a controller needs no
        // attribute of its own; what it must never do is opt out silently.
        var offenders = new List<string>();

        foreach (var file in ControllerFiles())
        {
            string body = File.ReadAllText(file.FullName);
            foreach (Match m in Regex.Matches(body, @"\[AllowAnonymous\]"))
            {
                // An anonymous endpoint is legitimate — sign-in, an OAuth
                // callback, an invite acceptance — but it must be deliberate,
                // and a reader must be able to see why without leaving the file.
                int start = Math.Max(0, m.Index - 600);
                string preceding = body[start..m.Index];
                if (!preceding.Contains("///", StringComparison.Ordinal))
                {
                    offenders.Add($"{file.Name}: an [AllowAnonymous] endpoint carries no explanation");
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join("\n  ", offenders));
    }

    [Fact]
    public void A_policy_is_never_named_by_a_bare_string()
    {
        // AuthorizationPolicies exists so that a mistyped policy name is a
        // compile error. Spelled as a literal it is a silently open endpoint
        // instead — the exact failure the constants were introduced to stop.
        var offenders = new List<string>();

        foreach (var file in ControllerFiles())
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file.FullName),
                @"\[Authorize\(\s*Policy\s*=\s*""(?<name>[^""]+)"""))
            {
                offenders.Add($"{file.Name}: Policy = \"{m.Groups["name"].Value}\" should be AuthorizationPolicies.{m.Groups["name"].Value}");
            }
        }

        Assert.True(offenders.Count == 0, string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_run_visibility_rule_exists_in_one_place_only()
    {
        // The Service account could book fills for a run but not read it: the
        // ownership rule was written once correctly and copied into the read
        // endpoints without its Service half, and a backtest died with 403 on
        // its own run.
        string body = File.ReadAllText(
            Path.Combine(ControllersDirectory().FullName, "SimulatorController.cs"));

        int inlineAdminChecks = Regex.Matches(body, @"User\.IsInRole\(""Admin""\)").Count;
        Assert.Equal(0, inlineAdminChecks);

        Assert.Single(Regex.Matches(body, @"private bool CallerMaySeeAnyRun\(\)"));
        Assert.Contains("UserRoles.Service", body);
    }
}
