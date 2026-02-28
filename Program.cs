// Based on https://stackoverflow.com/questions/77627964/how-to-activate-my-roles-in-the-privileged-identity-management-in-azure-from-con

using System.CommandLine;
using System.CommandLine.Parsing;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Authorization;
using Azure.ResourceManager.Authorization.Models;

// Default subscriptions when none are provided as arguments
string[] defaultSubscriptions =
[
    "759748fa-fab2-4225-b0e1-6a7b560f9a47", // msa-001766 (Dev/Dev-Int/Dev-Test)
    "30274081-925c-418f-9d14-1bd830051c6c", // msa-001767 (Pre-Prod / Hotfix)
    "5271b72d-a0d6-4ee7-adae-7a9af717eb0f", // msa-001768 (Prod)
];

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