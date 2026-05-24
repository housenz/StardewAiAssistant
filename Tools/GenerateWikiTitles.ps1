param(
    [string]$OutputPath = "Data/wiki-titles.json"
)

$ErrorActionPreference = "Stop"

$apiUrl = "https://wiki.biligame.com/stardewvalley/api.php"
$titles = New-Object 'System.Collections.Generic.List[string]'
$webClient = [System.Net.WebClient]::new()
$webClient.Encoding = [System.Text.Encoding]::UTF8
$continue = $null

do {
    $query = @{
        action = "query"
        format = "json"
        list = "allpages"
        apnamespace = "0"
        apfilterredir = "nonredirects"
        aplimit = "500"
    }

    if ($continue) {
        $query.apcontinue = $continue
    }

    $uriBuilder = [System.UriBuilder]$apiUrl
    $uriBuilder.Query = ($query.GetEnumerator() | ForEach-Object {
        [System.Uri]::EscapeDataString($_.Key) + "=" + [System.Uri]::EscapeDataString($_.Value)
    }) -join "&"

    $responseJson = $webClient.DownloadString($uriBuilder.Uri.AbsoluteUri)
    $response = $responseJson | ConvertFrom-Json
    foreach ($page in @($response.query.allpages)) {
        if (-not [string]::IsNullOrWhiteSpace($page.title)) {
            $titles.Add($page.title.Trim())
        }
    }

    $continue = $response.continue.apcontinue
} while ($continue)

$webClient.Dispose()

$distinctTitles = $titles |
    Sort-Object -Unique |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

$fullOutputPath = Join-Path (Get-Location) $OutputPath
$outputDirectory = Split-Path -Parent $fullOutputPath
if (-not (Test-Path $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}

$json = $distinctTitles | ConvertTo-Json -Depth 2
[System.IO.File]::WriteAllText($fullOutputPath, $json, [System.Text.UTF8Encoding]::new($false))

Write-Host "Generated $($distinctTitles.Count) wiki titles at $fullOutputPath"
