# Contract 12 — Infrastructure (Bicep / AVM) & docs

**Tasks:** T033–T037. Create an `azd`-ready Bicep deployment that provisions Azure Functions on the
**Flex Consumption** plan, identity-first (secret-free) connections, and the supporting resources —
composed of **Azure Verified Modules (AVM)** wherever one exists. Then write `azure.yaml` and the README.

## Hosting decision (record this in the README)
Deploy to **Azure Functions, Flex Consumption**. Both Oracle BI Publisher and Microsoft Graph are
public endpoints reached over REST (no native deps), so serverless fits: scale-to-zero, per-instance
concurrency, identity-based connections, KEDA-style scaling on queue depth, no image lifecycle.
Revisit (containerise on Container Apps/AKS) only if a dependency goes private or a native dependency
(Oracle client, PDF fonts) is introduced.

## `azure.yaml`
```yaml
name: oraclebi-universalprint
metadata:
  template: oraclebi-universalprint@1.0.0
infra:
  provider: bicep
  path: infra
services:
  functions:
    project: ./src/OracleBi.UniversalPrint.Functions
    language: dotnet
    host: function
```

## `infra/bicepconfig.json`
Enable the Microsoft Graph Bicep extension (so `graph-roles.bicep` can grant app roles).

## `infra/main.bicep` (`targetScope = 'subscription'`)
Params: `environmentName`, `location`, `universalPrintTenantId = tenant().tenantId`,
`universalPrintDefaultPrinterId`, `oracleBiBaseUrl`, `oracleBiUsername`, `@secure() oracleBiPassword`,
`grantGraphAppRoles = true`, `enableNetworkIsolation = false`.
- `tags = { 'azd-env-name': environmentName }`; `resourceToken = toLower(uniqueString(subscription().id, environmentName, location))`.
- Create `rg-${environmentName}`.
- `module network 'network.bicep' = if (enableNetworkIsolation)` (scope rg).
- `module resources 'resources.bicep'` (scope rg) passing all params + the network outputs (guarded by
  `enableNetworkIsolation`).
- `module graphRoles 'graph-roles.bicep' = if (grantGraphAppRoles)` passing `functionAppPrincipalId`.
- Outputs: `AZURE_LOCATION`, `AZURE_TENANT_ID`, `AZURE_RESOURCE_GROUP`, `SERVICE_FUNCTIONS_NAME`,
  `SERVICE_FUNCTIONS_URI`, `AZURE_STORAGE_ACCOUNT`.

## `infra/resources.bicep` (scope: resource group)
Provision with AVM modules (use current published versions):
- **Log Analytics** — `avm/res/operational-insights/workspace` (`log-${resourceToken}`, 30-day retention).
- **Application Insights** — `avm/res/insights/component` (workspace-based).
- **Key Vault** — `avm/res/key-vault/vault` (`kv-${resourceToken}`, RBAC auth, purge protection,
  standard sku) with secret `oracle-bi-password = oracleBiPassword`. `publicNetworkAccess` and
  `networkAcls` follow `enableNetworkIsolation`; add a `vault` private endpoint when isolated.
- **Storage** — `avm/res/storage/storage-account` (`st${resourceToken}`, Standard_LRS, TLS1_2,
  `allowBlobPublicAccess=false`, **`allowSharedKeyAccess=false`**). Containers: `app-package`
  (deployment), `idempotency`. Queues: `print-poll`, `print-submit`, `print-poll-deadletter`. Add
  `blob` + `queue` private endpoints when isolated.
- **Flex Consumption plan** — `avm/res/web/serverfarm` (`plan-${resourceToken}`, skuName `FC1`,
  kind `functionapp`, `reserved: true`).
- **Function app** — `avm/res/web/site` (`func-${resourceToken}`, kind `functionapp,linux`, tag
  `azd-service-name: functions`, system-assigned identity, `httpsOnly`, **basic publishing disabled**
  for ftp + scm). `functionAppConfig`: deployment from the `app-package` blob container via
  `SystemAssignedIdentity`; `scaleAndConcurrency` (maximumInstanceCount, instanceMemoryMB);
  `runtime { name: 'dotnet-isolated', version: '10.0' }`. The `appsettings` config wires
  `applicationInsightResourceId`, `storageAccountResourceId` with
  `storageAccountUseIdentityAuthentication: true`, and the app settings below (identity-based, no keys):
  - `QueueStorage__queueServiceUri`, `Queues__QueueServiceUri`, `Queues__BlobServiceUri`
  - `Queues__PollQueueName/SubmitQueueName/DeadLetterQueueName/IdempotencyContainerName`
  - `UniversalPrint__UseManagedIdentity=true`, `UniversalPrint__TenantId`, `UniversalPrint__DefaultPrinterId`
  - `OracleBi__BaseUrl`, `OracleBi__Username`, and `OracleBi__Password` as a
    `@Microsoft.KeyVault(SecretUri=...)` reference
  - `Polling__*`, `PrintSecurity__*` as needed.
- **Role assignments** (managed identity, least privilege): Storage **Queue Data Contributor**
  (`974c5e8b-45b9-4653-ba55-5f855dd0fb88`) on the storage account, and Key Vault **Secrets User**
  (`4633458b-17de-408a-b874-0445c86b69e6`) on the vault. (The site module grants Storage **Blob Data
  Owner** automatically; no Table role — the app doesn't use tables.)
- Outputs: `functionAppName`, `functionAppUri`, `functionAppPrincipalId`, `storageAccountName`.

## `infra/graph-roles.bicep`
`extension microsoftGraphV1`. Grant the function app's managed identity the Universal Print app roles
`PrintJob.ReadWrite.All` and `Printer.Read.All` via `Microsoft.Graph/appRoleAssignedTo@v1.0`, looking
up the Graph service principal by well-known appId `00000003-0000-0000-c000-000000000000`. Param:
`functionAppPrincipalId`. (Requires a deployer with directory privileges; README documents the
`grantGraphAppRoles=false` out-of-band fallback.)

## `infra/network.bicep` (opt-in)
`avm/res/network/virtual-network` with a Flex-integration subnet (delegated to
`Microsoft.App/environments`) and a private-endpoint subnet, plus `avm/res/network/private-dns-zone`
for blob, queue and vault. Output the subnet + DNS zone resource ids consumed by `resources.bicep`.

## `README.md`
Document: architecture (mermaid), project layout, retry/telemetry best practices + the metrics table,
status-polling design, DLQ logic, monitoring/alerting (KQL + metric + queue-depth alerts), dashboard
widgets, notification options, correlation via `CorrelationId`, setup/prereqs, local run (`dotnet build`,
`func start`, sample `curl`), the hosting decision, what the infra provisions, security hardening
table, and `azd up` deploy steps (including `azd env set` for printer + Oracle BI values).

## Acceptance

- `az bicep build --file infra/main.bicep` (or the Bicep linter) succeeds.
- `azd up` provisions the resource group and deploys the Functions app; the app runs with
  `UniversalPrint:UseManagedIdentity=true` and no client secret.
