# MultiSubAutoPIM

**Bulk-activate Azure PIM roles across multiple subscriptions in one shot.**

No more clicking through the portal for each role, for each subscription. Run once, activate everything.

---

## What It Does

MultiSubAutoPIM enumerates your eligible Privileged Identity Management (PIM) roles across one or more Azure subscriptions, skips any that are already active, and self-activates the rest — all in a single command.

| Feature | Detail |
|---|---|
| **Multi-subscription** | Processes N subscriptions sequentially |
| **Smart skip** | Detects already-activated roles and skips them |
| **Scope-aware** | Handles both subscription-level and resource-group-level role assignments |
| **Conflict-safe** | Catches "already exists" API errors gracefully |
| **Zero config** | Falls back to hardcoded subscription IDs when no arguments are given |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- An Azure account with PIM-eligible role assignments
- Authenticated via one of:
  - `az login` (Azure CLI)
  - Visual Studio / VS Code credential
  - Environment variables (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, etc.)
  - Interactive browser (fallback)

## Quick Start

```bash
# Clone & build
git clone <repo-url> && cd MultiSubAutoPIM
dotnet build

# Activate PIM roles on the default subscriptions
dotnet run

# Activate PIM roles on specific subscriptions
dotnet run -- <subscription-id-1> <subscription-id-2> ...
```

## Publish a Single-File Binary

```bash
# macOS Apple Silicon
dotnet publish -c Release -r osx-arm64 --self-contained \
  -p:PublishSingleFile=true -p:PublishTrimmed=true

# macOS Intel
dotnet publish -c Release -r osx-x64 --self-contained \
  -p:PublishSingleFile=true -p:PublishTrimmed=true

# Windows
dotnet publish -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:PublishTrimmed=true

# Linux
dotnet publish -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:PublishTrimmed=true
```

Output lands in `bin/Release/net10.0/<rid>/publish/`.

## Usage Examples

```bash
# Use default hardcoded subscriptions
./MultiSubAutoPIM

# Pass one subscription
./MultiSubAutoPIM 759748fa-fab2-4225-b0e1-6a7b560f9a47

# Pass multiple subscriptions
./MultiSubAutoPIM \
  759748fa-fab2-4225-b0e1-6a7b560f9a47 \
  30274081-925c-418f-9d14-1bd830051c6c \
  5271b72d-a0d6-4ee7-adae-7a9af717eb0f
```

### Sample Output

```
Current Subscription: msa-001766 (Dev/Dev-Int/Dev-Test)
  ✓ Activated 'Contributor'
  ✓ Skipping 'Reader' - Already active
  ✓ Activated 'Key Vault Secrets User'
Current Subscription: msa-001767 (Pre-Prod / Hotfix)
  ✓ Activated 'Contributor'
  ✗ Failed to activate 'Owner': Requestor does not have permission...
Current Subscription: msa-001768 (Prod)
  ✓ Skipping 'Contributor' - Already active
```

## Configuration

Edit the `defaultSubscriptions` array in [Program.cs](Program.cs) to change the fallback subscription IDs:

```csharp
string[] defaultSubscriptions =
[
    "your-subscription-id-1",
    "your-subscription-id-2",
];
```

All activations use a **24-hour duration** with justification `"Needed for work."`. Adjust the `Duration` and `Justification` fields in the `RoleAssignmentScheduleRequestData` block if needed.

## Dependencies

| Package | Purpose |
|---|---|
| `Azure.Identity` | Authentication (`DefaultAzureCredential`) |
| `Azure.ResourceManager` | ARM client & resource model |
| `Azure.ResourceManager.Authorization` | PIM role eligibility & activation APIs |

## Credits

Based on [this Stack Overflow answer](https://stackoverflow.com/questions/77627964/how-to-activate-my-roles-in-the-privileged-identity-management-in-azure-from-con), converted from PowerShell to a compiled .NET console app.

## License

MIT
