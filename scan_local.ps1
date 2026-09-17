$ErrorActionPreference = 'Stop'
$cecil = "C:\Users\Administrator\.nuget\packages\mono.cecil\0.11.5\lib\net40\Mono.Cecil.dll"
Add-Type -Path $cecil

$plugins = "D:\Steam\steamapps\common\Casualties Unknown Demo\BepInEx\plugins"
$core = "D:\Steam\steamapps\common\Casualties Unknown Demo\BepInEx\core"
$managed = "D:\Steam\steamapps\common\Casualties Unknown Demo\CasualtiesUnknown_Data\Managed"

$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.AddSearchDirectory($plugins)
$resolver.AddSearchDirectory($core)
$resolver.AddSearchDirectory($managed)
$params = New-Object Mono.Cecil.ReaderParameters
$params.AssemblyResolver = $resolver
$params.InMemory = $true

$results = New-Object System.Collections.ArrayList
Get-ChildItem -Path $plugins -Recurse -Include *.dll, *.dll_disabled | ForEach-Object {
    $path = $_.FullName
    $guid = ""; $pname = ""; $pver = ""; $asmver = ""
    try {
        $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($path, $params)
        $asmver = $asm.Name.Version.ToString()
        foreach ($t in $asm.MainModule.Types) {
            $attr = $t.CustomAttributes | Where-Object { $_.AttributeType.Name -eq "BepInPluginAttribute" } | Select-Object -First 1
            if ($attr -and $attr.ConstructorArguments.Count -ge 3) {
                $guid = [string]$attr.ConstructorArguments[0].Value
                $pname = [string]$attr.ConstructorArguments[1].Value
                $pver = [string]$attr.ConstructorArguments[2].Value
                break
            }
        }
        $asm.Dispose()
    } catch { return }

    $enabled = -not ($_.Name -match "_disabled$")
    $null = $results.Add(@{
        guid = $guid; name = if ($pname) { $pname } else { $_.BaseName -replace "_disabled$" , "" }
        version = $pver; asmVersion = $asmver; file = $_.Name
        enabled = $enabled; isPlugin = [bool]$guid
    })
}

$json = ConvertTo-Json @($results) -Depth 3
[System.IO.File]::WriteAllText("D:\AS\ASSV\local_mods.json", $json, [System.Text.Encoding]::UTF8)
Write-Output ("SCANNED " + $results.Count)
