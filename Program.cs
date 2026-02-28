// Based on https://stackoverflow.com/questions/77627964/how-to-activate-my-roles-in-the-privileged-identity-management-in-azure-from-con

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Authorization;
using Azure.ResourceManager.Authorization.Models;

// Load default subscriptions from config.json (not tracked by git).
// Falls back to config.template.json if config.json is missing.
string[] defaultSubscriptions = LoadSubscriptionsFromConfig();

var subsOption = new Option<string[]>("-s", "--subscription")
{
    Description = "One or more subscription IDs (defaults to hardcoded list)",
    AllowMultipleArgumentsPerToken = true,
};

var rolesOption = new Option<string[]>("-r", "--role")
{
    Description = "One or more role display names to activate (defaults to all eligible)",
    AllowMultipleArgumentsPerToken = true,
};

var rootCommand = new RootCommand("Bulk-activate Azure PIM roles across multiple subscriptions")
{
    subsOption,
    rolesOption
};

rootCommand.SetAction(async (ParseResult parseResult) =>
{
    var subs = parseResult.GetValue(subsOption) ?? [];
    var roles = parseResult.GetValue(rolesOption) ?? [];

    var subscriptions = subs.Length > 0 ? subs : defaultSubscriptions;
    var roleFilter = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase);

    await ActivatePimRolesAsync(subscriptions, roleFilter);
});

return rootCommand.Parse(args).Invoke();

// ---------------------------------------------------------------------------

static async Task ActivatePimRolesAsync(string[] subscriptions, HashSet<string> roleFilter)
{
    var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        ExcludeInteractiveBrowserCredential = false,
    });

    var armClient = new ArmClient(credential);

    // Cache of scope → (roleDefinitionId → maxDuration) from PIM policy assignments
    var policyDurationCache = new Dictionary<string, Dictionary<string, TimeSpan>>(StringComparer.OrdinalIgnoreCase);

    foreach (var subId in subscriptions)
    {
        var subscriptionResource = armClient.GetSubscriptionResource(new ResourceIdentifier($"/subscriptions/{subId}"));

        try
        {
            var sub = (await subscriptionResource.GetAsync()).Value;
            WriteColored($"Current Subscription: {sub.Data.DisplayName}", ConsoleColor.Green);
        }
        catch
        {
            continue; // Ignore errors like "Please provide a valid tenant or a valid subscription."
        }

        // Get all active role assignment instances to check if already activated
        var activeAssignments = new List<(ResourceIdentifier RoleDefinitionId, string? Scope)>();
        try
        {
            await foreach (var instance in subscriptionResource.GetRoleAssignmentScheduleInstances().GetAllAsync("asTarget()"))
            {
                if ("Provisioned".Equals(instance.Data.Status?.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    activeAssignments.Add((instance.Data.RoleDefinitionId, instance.Data.Scope));
                }
            }
        }
        catch { /* ignore errors fetching active assignments */ }

        var eligibleSchedules = new List<RoleEligibilityScheduleResource>(); // Get eligible role schedules filtered to subscription and resource group scope types
        try
        {
            await foreach (var schedule in subscriptionResource.GetRoleEligibilitySchedules().GetAllAsync("asTarget()"))
            {
                var scopeType = schedule.Data.ExpandedProperties?.ScopeType;

                if (scopeType == RoleManagementScopeType.Subscription ||
                    scopeType == RoleManagementScopeType.ResourceGroup)
                {
                    eligibleSchedules.Add(schedule);
                }
            }
        }
        catch { /* ignore errors fetching eligible schedules */ }

        // Pre-fetch PIM policy max durations for each unique scope
        var uniqueScopes = eligibleSchedules
            .Select(s => s.Data.Scope ?? $"/subscriptions/{subId}")
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var policyScope in uniqueScopes)
        {
            if (!policyDurationCache.ContainsKey(policyScope))
            {
                policyDurationCache[policyScope] = await GetMaxActivationDurationsAsync(armClient, policyScope);
            }
        }

        var grouped = eligibleSchedules     // Group by (RoleName, Scope) and take the first of each group
            .GroupBy(s => (s.Data.ExpandedProperties?.RoleDefinitionDisplayName, s.Data.Scope))
            .Select(g => g.First());

        foreach (var eligible in grouped)
        {
            var roleName = eligible.Data.ExpandedProperties?.RoleDefinitionDisplayName;
            var scope = eligible.Data.Scope;

            // If role filter is specified, skip roles not in the filter
            if (roleFilter.Count > 0 && !roleFilter.Contains(roleName ?? ""))
            {
                continue;
            }

            // Check if this role is already active at this scope
            var isActive = activeAssignments.Any(a =>
                eligible.Data.RoleDefinitionId is not null &&
                a.RoleDefinitionId == eligible.Data.RoleDefinitionId &&
                string.Equals(a.Scope, scope, StringComparison.OrdinalIgnoreCase));

            if (isActive)
            {
                WriteColored($"  ✓ Skipping '{roleName}' - Already active", ConsoleColor.Yellow);
                continue;
            }

            try
            {
                // Look up the max activation duration from PIM policy; fall back to 8h if unknown
                var maxDuration = TimeSpan.FromHours(8);
                var roleDefKey = eligible.Data.RoleDefinitionId?.ToString() ?? "";
                var lookupScope = scope ?? $"/subscriptions/{subId}";
                if (policyDurationCache.TryGetValue(lookupScope, out var durations) &&
                    durations.TryGetValue(roleDefKey, out var policyMax))
                {
                    maxDuration = policyMax;
                }

                var requestData = new RoleAssignmentScheduleRequestData
                {
                    RoleDefinitionId = eligible.Data.RoleDefinitionId,
                    PrincipalId = eligible.Data.PrincipalId ?? Guid.Empty,
                    RequestType = RoleManagementScheduleRequestType.SelfActivate,
                    Justification = "Needed for work.",
                    StartOn = DateTimeOffset.UtcNow,
                    ExpirationType = RoleManagementScheduleExpirationType.AfterDuration,
                    Duration = maxDuration,
                };

                // Create the request at the appropriate scope (subscription or resource group)
                ArmResource scopeResource = scope is not null &&
                    scope.Contains("/resourceGroups/", StringComparison.OrdinalIgnoreCase)
                        ? armClient.GetResourceGroupResource(new ResourceIdentifier(scope))
                        : subscriptionResource;

                await scopeResource.GetRoleAssignmentScheduleRequests()
                    .CreateOrUpdateAsync(WaitUntil.Completed, Guid.NewGuid().ToString(), requestData);

                WriteColored($"  ✓ Activated '{roleName}' ({maxDuration.TotalHours}h)", ConsoleColor.Green);
            }
            catch (RequestFailedException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                WriteColored($"  ✓ Skipping '{roleName}' - Already active", ConsoleColor.Yellow);
            }
            catch (Exception ex)
            {
                WriteColored($"  ✗ Failed to activate '{roleName}': {ex.Message}", ConsoleColor.Red);
            }
        }
    }
}

static void WriteColored(string message, ConsoleColor color)
{
    Console.ForegroundColor = color;
    Console.WriteLine(message);
    Console.ResetColor();
}

/// <summary>
/// Fetches PIM policy assignments for a given scope and returns a dictionary
/// mapping each role definition ID to its maximum allowed activation duration.
/// The duration comes from the RoleManagementPolicyExpirationRule whose target
/// level is "Assignment" (i.e. activation, not eligibility).
/// </summary>
static async Task<Dictionary<string, TimeSpan>> GetMaxActivationDurationsAsync(ArmClient client, string scope)
{
    var result = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
    try
    {
        var scopeId = new ResourceIdentifier(scope);
        var policyAssignments = client.GetRoleManagementPolicyAssignments(scopeId);

        await foreach (var assignment in policyAssignments.GetAllAsync())
        {
            var roleDefId = assignment.Data.RoleDefinitionId?.ToString();
            if (roleDefId is null) continue;

            // Find the expiration rule for activation (Assignment level)
            var expirationRule = assignment.Data.EffectiveRules
                .OfType<RoleManagementPolicyExpirationRule>()
                .FirstOrDefault(r => r.Target?.Level == RoleManagementAssignmentLevel.Assignment);

            if (expirationRule?.MaximumDuration is { } maxDuration)
            {
                result[roleDefId] = maxDuration;
            }
        }
    }
    catch { /* ignore errors fetching policy assignments */ }
    return result;
}

/// <summary>
/// Loads subscription IDs from config.json (user-specific, git-ignored).
/// Falls back to config.template.json if config.json is missing.
/// </summary>
static string[] LoadSubscriptionsFromConfig()
{
    var exeDir = AppContext.BaseDirectory;
    var workDir = Directory.GetCurrentDirectory();

    // Look for config.json next to the executable first, then in the working directory
    string? configPath = null;
    foreach (var dir in new[] { workDir, exeDir })
    {
        var candidate = Path.Combine(dir, "config.json");
        if (File.Exists(candidate)) { configPath = candidate; break; }
    }

    // Fall back to config.template.json
    if (configPath is null)
    {
        foreach (var dir in new[] { workDir, exeDir })
        {
            var candidate = Path.Combine(dir, "config.template.json");
            if (File.Exists(candidate)) { configPath = candidate; break; }
        }
        if (configPath is not null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"⚠ config.json not found — using template: {configPath}");
            Console.WriteLine("  Copy config.template.json to config.json and add your subscription IDs.");
            Console.ResetColor();
        }
    }

    if (configPath is null)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("✗ No config.json or config.template.json found. Supply subscriptions via -s or create a config file.");
        Console.ResetColor();
        return [];
    }

    try
    {
        var json = File.ReadAllText(configPath);
        using var doc = JsonDocument.Parse(json);
        var subs = doc.RootElement.GetProperty("subscriptions");
        return subs.EnumerateArray()
            .Select(e => e.GetProperty("id").GetString()!)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToArray();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"✗ Failed to read {configPath}: {ex.Message}");
        Console.ResetColor();
        return [];
    }
}