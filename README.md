# MultiSubAutoPIM

**Bulk-activate Azure PIM roles across multiple subscriptions in one shot.**

No more clicking through the portal for each role, for each subscription. Run once, activate everything.

---

## What It Does

MultiSubAutoPIM enumerates your eligible Privileged Identity Management (PIM) roles across one or more Azure subscriptions, skips any that are already active, and self-activates the rest — all in a single command.

| Feature | Detail |
|---|---|
| **Multi-subscription** | Processes N subscriptions sequentially |
| **Role filtering** | Activate only specific roles by display name with `-r` |
| **Smart skip** | Detects already-activated roles and skips them |
| **Scope-aware** | Handles both subscription-level and resource-group-level role assignments |
| **Policy-aware duration** | Reads the maximum allowed activation duration from PIM policy settings per role — no hardcoded values |
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

# Activate all eligible PIM roles on the default subscriptions
dotnet run

# Show help
dotnet run -- --help
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
# Use default hardcoded subscriptions, activate all eligible roles
./MultiSubAutoPIM

# Activate all roles on specific subscriptions
./MultiSubAutoPIM -s 759748fa-fab2-4225-b0e1-6a7b560f9a47 \
                  -s 30274081-925c-418f-9d14-1bd830051c6c

# Activate only Contributor across all default subscriptions
./MultiSubAutoPIM -r Contributor

# Activate specific roles on specific subscriptions
./MultiSubAutoPIM -s 759748fa-fab2-4225-b0e1-6a7b560f9a47 \
                  -r Contributor -r "Key Vault Secrets User"
```

### CLI Options

| Flag | Description |
|---|---|
| `-s`, `--subscription` | One or more subscription IDs (repeatable) |
| `-r`, `--role` | One or more role display names to activate (repeatable) |
| `-h`, `--help` | Show help and usage information |
| `--version` | Show version information |

### Sample Output

```
Current Subscription: msa-001766 (Dev/Dev-Int/Dev-Test)
  ✓ Activated 'Contributor' (8h)
  ✓ Skipping 'Reader' - Already active
  ✓ Activated 'Key Vault Secrets User' (24h)
Current Subscription: msa-001767 (Pre-Prod / Hotfix)
  ✓ Activated 'Contributor' (8h)
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

Each activation automatically uses the **maximum allowed duration** defined in the PIM policy for that specific role and scope (e.g., 8h for Contributor, 24h for Key Vault Secrets User). If the policy cannot be read, it falls back to **8 hours**. The justification defaults to `"Needed for work."` — adjust it in the `RoleAssignmentScheduleRequestData` block if needed.

## Dependencies

| Package | Purpose |
|---|---|
| `Azure.Identity` | Authentication (`DefaultAzureCredential`) |
| `Azure.ResourceManager` | ARM client & resource model |
| `Azure.ResourceManager.Authorization` | PIM role eligibility & activation APIs |
| `System.CommandLine` | CLI argument parsing (`-s`, `-r`, `--help`) |

## Credits

Based on [this Stack Overflow answer](https://stackoverflow.com/questions/77627964/how-to-activate-my-roles-in-the-privileged-identity-management-in-azure-from-con), converted from PowerShell to a compiled .NET console app.

## License

MIT
