# Architecture

## Overview

MultiSubAutoPIM is a single-file .NET console application that orchestrates Azure PIM role activations across multiple subscriptions using the Azure Resource Manager SDK.

## Flow Diagram

```mermaid
flowchart TD
    Start([Start]) --> ParseArgs{CLI args provided?}
    ParseArgs -- Yes --> UseArgs[Use provided subscription IDs]
    ParseArgs -- No --> UseDefaults[Use hardcoded subscription IDs]
    UseArgs --> Auth
    UseDefaults --> Auth

    Auth[Authenticate via DefaultAzureCredential] --> Loop

    subgraph Loop ["For each Subscription"]
        direction TB
        Resolve[Resolve SubscriptionResource] --> Fetch{Fetch subscription metadata}
        Fetch -- Error --> Skip[Skip subscription]
        Fetch -- OK --> PrintSub["Print subscription name 🟢"]

        PrintSub --> GetActive[Get active role assignment instances\nFilter: Status = Provisioned]
        GetActive --> GetEligible[Get eligible role schedules\nFilter: Scope = Subscription | ResourceGroup]
        GetEligible --> Group[Group by RoleName + Scope\nTake first per group]

        Group --> RoleLoop

        subgraph RoleLoop ["For each Eligible Role"]
            direction TB
            CheckActive{Role already active\nat this scope?}
            CheckActive -- Yes --> SkipRole["Print skip message 🟡"]
            CheckActive -- No --> Activate

            subgraph Activate [Self-Activate]
                direction TB
                BuildReq[Build RoleAssignmentScheduleRequest\n• SelfActivate\n• 24h duration\n• Justification] --> DetectScope{Scope contains\n/resourceGroups/?}
                DetectScope -- Yes --> RGScope[Use ResourceGroupResource]
                DetectScope -- No --> SubScope[Use SubscriptionResource]
                RGScope --> Submit[Submit activation request]
                SubScope --> Submit
            end

            Submit --> Result{Result}
            Result -- Success --> PrintOK["Print activated 🟢"]
            Result -- Already exists --> PrintDup["Print skip 🟡"]
            Result -- Other error --> PrintErr["Print error 🔴"]
        end
    end

    Skip --> Loop
    Loop --> Done([Done])
```

## Component Breakdown

```
Program.cs (top-level statements)
├── Argument Parsing ........... CLI args → subscription ID list
├── Authentication ............. DefaultAzureCredential (az login / env / browser)
├── Subscription Loop
│   ├── Metadata Fetch ......... Validates subscription access
│   ├── Active Assignments ..... RoleAssignmentScheduleInstances (Status=Provisioned)
│   ├── Eligible Schedules ..... RoleEligibilitySchedules (Subscription + ResourceGroup)
│   ├── Grouping ............... Dedup by (RoleName, Scope)
│   └── Activation Loop
│       ├── Skip Check ......... Compare against active assignments
│       ├── Request Build ...... RoleAssignmentScheduleRequestData
│       ├── Scope Resolution ... Subscription vs ResourceGroup resource
│       └── Error Handling ..... "already exists" → skip, other → report
└── WriteColored() ............. Console helper with color support
```

## Azure SDK Call Chain

```mermaid
sequenceDiagram
    participant App as MultiSubAutoPIM
    participant Cred as DefaultAzureCredential
    participant ARM as Azure Resource Manager
    participant PIM as PIM / Authorization API

    App->>Cred: Acquire token
    Cred-->>App: Bearer token

    loop Each Subscription
        App->>ARM: GET /subscriptions/{id}
        ARM-->>App: Subscription metadata

        App->>PIM: GET roleAssignmentScheduleInstances?$filter=asTarget()
        PIM-->>App: Active assignments

        App->>PIM: GET roleEligibilitySchedules?$filter=asTarget()
        PIM-->>App: Eligible roles

        loop Each Eligible Role (not already active)
            App->>PIM: PUT roleAssignmentScheduleRequests/{guid}
            Note over PIM: RequestType: SelfActivate<br/>Duration: PT24H
            PIM-->>App: 200 OK / 409 Conflict / Error
        end
    end
```

## Key Design Decisions

| Decision | Rationale |
|---|---|
| **Top-level statements** | Keeps the tool as a simple single-file script, no ceremony |
| **DefaultAzureCredential** | Chains multiple auth methods automatically, works everywhere |
| **Group-then-first** | Mirrors the PowerShell `Group-Object \| Select -First` dedup pattern |
| **Scope resolution** | PIM API requires the request to be scoped to the exact resource (subscription or resource group) |
| **Catch "already exists"** | Race condition between the active-check and the activation request |
| **Silent catch on fetch** | Non-accessible subscriptions are skipped without breaking the loop |
