# Contract 01 — Solution scaffold

**Tasks:** T001–T005. Create the solution skeleton: a class library, an Azure Functions
isolated-worker app, and an xUnit test project, wired into a `.sln`.

## Steps

1. Create solution `OracleBi.UniversalPrint.sln` with solution folders `src` and `tests`.
2. Create the three projects below with the exact properties and package references.
3. Add project references and `dotnet build` to confirm everything restores.

### `src/OracleBi.UniversalPrint.Core/OracleBi.UniversalPrint.Core.csproj` (class library)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <RootNamespace>OracleBi.UniversalPrint</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="OracleBi.UniversalPrint.Tests" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Azure.Identity" Version="1.21.0" />
    <PackageReference Include="Azure.Storage.Queues" Version="12.27.0" />
    <PackageReference Include="Azure.Storage.Blobs" Version="12.29.0" />
    <PackageReference Include="Polly.Core" Version="8.5.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.3" />
    <PackageReference Include="Microsoft.Extensions.Http" Version="10.0.3" />
    <PackageReference Include="Microsoft.Extensions.Options.ConfigurationExtensions" Version="10.0.3" />
    <PackageReference Include="Microsoft.Extensions.Options.DataAnnotations" Version="10.0.3" />
    <PackageReference Include="Azure.Monitor.OpenTelemetry.Exporter" Version="1.8.1" />
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.16.0" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Http" Version="1.15.1" />
  </ItemGroup>
</Project>
```

### `src/OracleBi.UniversalPrint.Functions/OracleBi.UniversalPrint.Functions.csproj` (Functions, isolated)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AzureFunctionsVersion>v4</AzureFunctionsVersion>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <RootNamespace>OracleBi.UniversalPrint.Functions</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Azure.Functions.Worker" Version="2.52.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Sdk" Version="2.0.7" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Storage.Queues" Version="5.5.4" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Http" Version="3.3.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.OpenTelemetry" Version="1.2.0" />
    <PackageReference Include="Azure.Monitor.OpenTelemetry.Exporter" Version="1.8.1" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\OracleBi.UniversalPrint.Core\OracleBi.UniversalPrint.Core.csproj" />
  </ItemGroup>
  <ItemGroup>
    <None Update="host.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>
    <None Update="local.settings.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
      <CopyToPublishDirectory>Never</CopyToPublishDirectory>
    </None>
  </ItemGroup>
</Project>
```

### `tests/OracleBi.UniversalPrint.Tests/OracleBi.UniversalPrint.Tests.csproj` (xUnit)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\OracleBi.UniversalPrint.Core\OracleBi.UniversalPrint.Core.csproj" />
  </ItemGroup>
</Project>
```

## Folder layout to honour in later contracts

```
src/OracleBi.UniversalPrint.Core/
  Abstractions/ Configuration/ DependencyInjection/ Idempotency/ Models/
  OracleBiIntegration/ Polling/ Queueing/ Resilience/ Telemetry/ UniversalPrintIntegration/
src/OracleBi.UniversalPrint.Functions/
tests/OracleBi.UniversalPrint.Tests/
```

## Acceptance

- `dotnet build` restores and compiles all three (empty) projects.
