# Builds the C# assembly Godot loads and opens the 3D match viewer.
#   powershell -File game\run.ps1                                   # team-4v4-mixed, seed 1
#   powershell -File game\run.ps1 --scenario big-10v10 --seed 4 --speed 2
#   powershell -File game\run.ps1 -Godot C:\path\Godot.exe
# Viewer options: --scenario <name or path> --seed N --speed X --zoom M --follow i --cut
#                 --shots 5,20 [--shot-dir dir]  (save PNG frames at these sim times and quit)
# No param() block on purpose: PowerShell would swallow the viewer's "--" options.
$ErrorActionPreference = 'Stop'
$godot = 'D:\tools\godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe'
$viewerArgs = @()
for ($i = 0; $i -lt $args.Count; $i++) {
    if ($args[$i] -eq '-Godot') { $godot = $args[++$i]; continue }
    $viewerArgs += [string]$args[$i]
}
if (-not (Test-Path -LiteralPath $godot)) { throw "Godot not found: $godot. Pass -Godot <path to Godot .NET 4.7>." }
# Godot runs the Debug build; without this step it starts the previous code.
dotnet build (Join-Path $PSScriptRoot 'SquadViewer.csproj') --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Viewer build failed.' }
if ($viewerArgs.Count -gt 0) { & $godot --path $PSScriptRoot -- @viewerArgs } else { & $godot --path $PSScriptRoot }
