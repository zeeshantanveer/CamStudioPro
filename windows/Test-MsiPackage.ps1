param([Parameter(Mandatory=$true)][string]$PackagePath)
$ErrorActionPreference = "Stop"
$path = (Resolve-Path -LiteralPath $PackagePath).Path
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase($path,0)
function Rows([string]$query,[int]$columns) {
    $view = $database.OpenView($query)
    [void]$view.Execute()
    try {
        while ($record = $view.Fetch()) {
            $row = @()
            for ($i=1;$i -le $columns;$i++) { $row += $record.StringData($i) }
            ,$row
        }
    } finally { [void]$view.Close() }
}
$properties = @{}
foreach ($row in Rows 'SELECT `Property`, `Value` FROM `Property`' 2) { $properties[$row[0]]=$row[1] }
if ($properties['ProductName'] -ne 'CamStudio Pro' -or $properties['ALLUSERS'] -ne '1') { throw "Wrong product or install scope." }
if ($properties['UpgradeCode'] -ne '{A8C872DD-5DB0-4BEA-9BAC-B311265346A1}') { throw "Wrong upgrade identity." }
$files = @(Rows 'SELECT `File`, `FileName` FROM `File`' 2)
$native = @($files | Where-Object { $_[1] -like '*CamStudio.VCam.Native.dll' })
if ($native.Count -ne 1) { throw "Missing native camera source." }
if (!($files | Where-Object { $_[1] -like '*coreclr.dll' })) { throw "Missing bundled .NET runtime." }
$registry = @(Rows 'SELECT `Root`, `Key`, `Name`, `Value` FROM `Registry`' 4)
$key = 'SOFTWARE\Classes\CLSID\{3c1f6a52-8e0b-4b7d-9f2a-6d4e8c1b7a35}\InprocServer32'
$server = @($registry | Where-Object { $_[0] -eq '2' -and $_[1] -eq $key -and !$_[2] })
if ($server.Count -ne 1 -or $server[0][3] -ne "[#$($native[0][0])]") { throw "Wrong camera registration." }
if (!($registry | Where-Object { $_[0] -eq '2' -and $_[1] -eq $key -and $_[2] -eq 'ThreadingModel' -and $_[3] -eq 'Both' })) { throw "Wrong camera threading model." }
Write-Host "PASS: MSI per-machine scope, upgrade identity, native source registration and bundled runtime."
Write-Host "Version: $($properties['ProductVersion']); ProductCode: $($properties['ProductCode'])"
