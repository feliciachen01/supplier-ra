$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http -ErrorAction Stop

$repoRoot = Split-Path -Parent $PSScriptRoot
$secretRoot = Join-Path $env:APPDATA 'Microsoft\UserSecrets'
$appProject = Join-Path $repoRoot 'src\SupplierResearch\SupplierResearch.csproj'
$probeProject = Join-Path $env:TEMP 'supplier-ra-m0-provider-check\supplier-ra-m0-provider-check.csproj'

function Get-ProjectSecretInfo([string]$projectPath, [string]$secretName) {
    if (-not (Test-Path -LiteralPath $projectPath)) { return $null }
    [xml]$project = Get-Content -LiteralPath $projectPath -Raw
    $idNode = $project.SelectSingleNode("//*[local-name()='UserSecretsId']")
    if ($null -eq $idNode -or [string]::IsNullOrWhiteSpace($idNode.InnerText)) { return $null }
    $file = Join-Path (Join-Path $secretRoot $idNode.InnerText) 'secrets.json'
    $values = @{}
    if (Test-Path -LiteralPath $file) {
        $document = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
        foreach ($property in $document.PSObject.Properties) { $values[$property.Name] = $property.Value }
    }
    [pscustomobject]@{ file = $file; values = $values; secretName = $secretName }
}

function Test-HeaderValue([string]$headerName, [string]$key) {
    if ([string]::IsNullOrEmpty($key) -or $key -cne $key.Trim()) { return $false }
    foreach ($character in $key.ToCharArray()) {
        if ([char]::IsControl($character)) { return $false }
    }
    $request = [System.Net.Http.HttpRequestMessage]::new()
    try {
        if ($headerName -eq 'Authorization') {
            $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $key)
        } else {
            $request.Headers.Add($headerName, $key)
        }
        return $true
    } catch {
        return $false
    } finally {
        $request.Dispose()
    }
}

function Read-MaskedKey([string]$prompt) {
    $secureInput = Read-Host $prompt -AsSecureString
    $pointer = [IntPtr]::Zero
    try {
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureInput)
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    } finally {
        if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
        if ($secureInput) { $secureInput.Dispose() }
    }
}

function Save-ProjectSecret([string]$projectPath, [string]$secretName, [string]$secretValue) {
    [xml]$project = Get-Content -LiteralPath $projectPath -Raw
    $id = $project.SelectSingleNode("//*[local-name()='UserSecretsId']").InnerText
    $directory = Join-Path $secretRoot $id
    $file = Join-Path $directory 'secrets.json'
    $values = @{}
    if (Test-Path -LiteralPath $file) {
        $document = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
        foreach ($property in $document.PSObject.Properties) { $values[$property.Name] = $property.Value }
    }
    $values[$secretName] = $secretValue
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $json = ConvertTo-Json -InputObject $values -Depth 8 -Compress
    [IO.File]::WriteAllText($file, $json, [Text.UTF8Encoding]::new($false))
}

function Protect-Message([string]$message, [string]$key) {
    if ([string]::IsNullOrWhiteSpace($message)) { return $null }
    if (-not [string]::IsNullOrEmpty($key)) { $message = $message.Replace($key, '[redacted]') }
    $message = [regex]::Replace($message, '(?i)Bearer\s+\S+', 'Bearer [redacted]')
    $message = [regex]::Replace($message, '(?i)(api[_ -]?key|token|authorization|x-goog-api-key)\s*[:=]\s*[^\s,;]+', '$1=[redacted]')
    $message = [regex]::Replace($message, 'https?://\S+', '[url]')
    $message = [regex]::Replace($message, '[\x00-\x1F\x7F]+', ' ').Trim()
    if ($message.Length -gt 240) { $message = $message.Substring(0, 240) }
    return $message
}

function Get-ProviderError([string]$body, [string]$key) {
    try {
        $json = $body | ConvertFrom-Json
        foreach ($name in @('detail', 'message', 'error')) {
            $property = $json.PSObject.Properties[$name]
            if ($null -ne $property) {
                if ($property.Value -is [string]) { return Protect-Message $property.Value $key }
                if ($null -ne $property.Value.message) { return Protect-Message ([string]$property.Value.message) $key }
            }
        }
    } catch { }
    return $null
}

function Invoke-OneCheck([string]$name, [string]$uri, [hashtable]$headers, [string]$key, [string]$jsonBody, [scriptblock]$summarize) {
    try {
        $response = Invoke-WebRequest -Method Post -Uri $uri -Headers $headers -ContentType 'application/json' `
            -Body $jsonBody -TimeoutSec 20 -MaximumRedirection 0 -UseBasicParsing
        $shape = & $summarize ([string]$response.Content)
        return [pscustomobject]@{ provider = $name; outcome = 'success'; status = [int]$response.StatusCode; shape = $shape }
    } catch {
        $status = $null
        $responseBody = $null
        if ($null -ne $_.Exception.Response) {
            $errorResponse = $_.Exception.Response
            try { $status = [int]$errorResponse.StatusCode } catch { }
            try {
                if ($null -ne $errorResponse.Content) {
                    $responseBody = $errorResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                } else {
                    $stream = $errorResponse.GetResponseStream()
                    if ($null -ne $stream) {
                        $reader = [IO.StreamReader]::new($stream)
                        $responseBody = $reader.ReadToEnd()
                        $reader.Dispose()
                    }
                }
            } catch { }
        }
        $baseException = $_.Exception.GetBaseException()
        $safeError = Get-ProviderError $responseBody $key
        if ($null -eq $safeError) { $safeError = Protect-Message $baseException.Message $key }
        $outcome = 'transport'
        if ($null -ne $status) { $outcome = 'upstream_http' }
        elseif ($baseException -is [TimeoutException] -or
            $baseException -is [System.Threading.Tasks.TaskCanceledException] -or
            $baseException -is [OperationCanceledException] -or
            ($baseException -is [Net.WebException] -and $baseException.Status -eq [Net.WebExceptionStatus]::Timeout)) {
            $outcome = 'timeout'
        }
        return [pscustomobject]@{ provider = $name; outcome = $outcome; status = $status; error = $safeError }
    }
}

$tavilyInfo = Get-ProjectSecretInfo $appProject 'TAVILY_API_KEY'
$geminiInfo = Get-ProjectSecretInfo $probeProject 'GEMINI_API_KEY'
if ($null -eq $tavilyInfo -or $null -eq $geminiInfo) {
    [pscustomobject]@{ status = 'secret_store_unavailable'; requestsMade = $false } | ConvertTo-Json -Compress
    exit 1
}

$tavilyKey = [string]$tavilyInfo.values[$tavilyInfo.secretName]
$geminiKey = [string]$geminiInfo.values[$geminiInfo.secretName]
$tavilyValid = Test-HeaderValue 'Authorization' $tavilyKey
$geminiValid = Test-HeaderValue 'x-goog-api-key' $geminiKey
$credentialsReplaced = $false

if (-not $tavilyValid -or -not $geminiValid) {
    $tavilyKey = Read-MaskedKey 'Stored key text is invalid. Paste the complete active Tavily API key (masked)'
    $geminiKey = Read-MaskedKey 'Paste the complete Gemini API key (masked)'
    $tavilyValid = Test-HeaderValue 'Authorization' $tavilyKey
    $geminiValid = Test-HeaderValue 'x-goog-api-key' $geminiKey
    if (-not $tavilyValid -or -not $geminiValid) {
        [pscustomobject]@{ status = 'replacement_rejected'; requestsMade = $false; reason = 'header_validation_failed' } | ConvertTo-Json -Compress
        exit 1
    }
    Save-ProjectSecret $appProject 'TAVILY_API_KEY' $tavilyKey
    Save-ProjectSecret $probeProject 'GEMINI_API_KEY' $geminiKey
    $credentialsReplaced = $true
}

# Both final header values are validated locally before either network request.
$tavilyHeaders = @{ Authorization = "Bearer $tavilyKey" }
$geminiHeaders = @{ 'x-goog-api-key' = $geminiKey }
$tavilyBody = @{
    query = 'supplier packaging sustainability'
    search_depth = 'basic'
    max_results = 1
    chunks_per_source = 1
    include_answer = $false
    include_raw_content = $false
    include_images = $false
    auto_parameters = $false
} | ConvertTo-Json -Compress
$tavilySummary = {
    param($content)
    $json = $content | ConvertFrom-Json
    [pscustomobject]@{
        topLevelFields = @($json.PSObject.Properties.Name)
        resultCount = @($json.results).Count
        firstResultFields = if (@($json.results).Count -gt 0) { @($json.results[0].PSObject.Properties.Name) } else { @() }
    }
}

$model = 'gemini-3.1-flash-lite'
$geminiBody = @{
    contents = @(@{ parts = @(@{ text = 'Return JSON with status set to ok.' }) })
    generationConfig = @{
        responseMimeType = 'application/json'
        responseSchema = @{ type = 'OBJECT'; properties = @{ status = @{ type = 'STRING' } }; required = @('status') }
        maxOutputTokens = 32
    }
} | ConvertTo-Json -Depth 8 -Compress
$geminiSummary = {
    param($content)
    $json = $content | ConvertFrom-Json
    [pscustomobject]@{
        modelVersion = $json.modelVersion
        candidateCount = @($json.candidates).Count
        firstCandidateFields = if (@($json.candidates).Count -gt 0) { @($json.candidates[0].PSObject.Properties.Name) } else { @() }
        usage = $json.usageMetadata
    }
}

$results = @(
    (Invoke-OneCheck 'Tavily' 'https://api.tavily.com/search' $tavilyHeaders $tavilyKey $tavilyBody $tavilySummary),
    (Invoke-OneCheck 'Gemini' "https://generativelanguage.googleapis.com/v1beta/models/${model}:generateContent" $geminiHeaders $geminiKey $geminiBody $geminiSummary)
)
$tavilyKey = $null
$geminiKey = $null
[pscustomobject]@{ status = 'complete'; credentialsReplaced = $credentialsReplaced; providers = $results } |
    ConvertTo-Json -Depth 8 -Compress
