$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$probeProject = Join-Path (Join-Path $env:TEMP 'supplier-ra-m0-provider-check') 'supplier-ra-m0-provider-check.csproj'
$appProject = Join-Path $repoRoot 'src/SupplierResearch/SupplierResearch.csproj'
if (-not (Test-Path -LiteralPath $probeProject) -or -not (Test-Path -LiteralPath $appProject)) {
    throw 'Required project configuration was not found.'
}

[xml]$probeXml = Get-Content -LiteralPath $probeProject -Raw
[xml]$appXml = Get-Content -LiteralPath $appProject -Raw
$probeId = [string]$probeXml.Project.PropertyGroup.UserSecretsId
$appId = [string]$appXml.Project.PropertyGroup.UserSecretsId
$probePath = Join-Path (Join-Path $env:APPDATA "Microsoft/UserSecrets/$probeId") 'secrets.json'
$appDirectory = Join-Path $env:APPDATA "Microsoft/UserSecrets/$appId"
$appPath = Join-Path $appDirectory 'secrets.json'
if (-not (Test-Path -LiteralPath $probePath) -or [string]::IsNullOrWhiteSpace($appId)) {
    throw 'Required local secret configuration was not found.'
}

$probeSecrets = Get-Content -LiteralPath $probePath -Raw | ConvertFrom-Json
$configuredKey = [string]$probeSecrets.TAVILY_API_KEY
if ([string]::IsNullOrEmpty($configuredKey)) {
    throw 'The configured comparison value is unavailable.'
}

$secureInput = Read-Host 'Paste the active Tavily key from the dashboard (input is masked)' -AsSecureString
$pointer = [IntPtr]::Zero
$enteredKey = $null
try {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureInput)
    $enteredKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    if ([string]::IsNullOrEmpty($enteredKey)) {
        throw 'No key was entered.'
    }

    $match = [string]::Equals($enteredKey, $configuredKey, [StringComparison]::Ordinal)
    $appSecrets = @{}
    if (Test-Path -LiteralPath $appPath) {
        $existingSecrets = Get-Content -LiteralPath $appPath -Raw | ConvertFrom-Json
        foreach ($property in $existingSecrets.PSObject.Properties) {
            $appSecrets[$property.Name] = $property.Value
        }
    }
    $appSecrets['TAVILY_API_KEY'] = $enteredKey
    [IO.Directory]::CreateDirectory($appDirectory) | Out-Null
    $json = ConvertTo-Json -InputObject $appSecrets -Compress
    [IO.File]::WriteAllText($appPath, $json, [Text.UTF8Encoding]::new($false))

    if ($match) { [Console]::WriteLine('match') } else { [Console]::WriteLine('no-match') }
}
finally {
    if ($pointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
    if ($secureInput) { $secureInput.Dispose() }
    $enteredKey = $null
    $configuredKey = $null
}
