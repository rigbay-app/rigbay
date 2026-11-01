# Prints the WCF contract METADATA (names, namespaces, attribute values, member shapes) of the few Intel XTU types the module mirrors
# in Rigbay.Module.Xtu/Intel/Contracts.cs. Read-only: the assemblies are loaded for reflection only (no code of theirs runs), and
# nothing of Intel's is copied or stored; the output is text for a human to read while writing the module's own contract file.
#
# Usage (Windows PowerShell 5.1, because the assemblies are .NET Framework):
#   powershell -ExecutionPolicy Bypass -File dump-contracts.ps1 -XtuDir <the Drivers folder of the XTU install> [-Out <file>]
# (the folder that holds XtuInterface.dll and XtuCommon.dll, under the Intel folder in Program Files; the module never needs it to build)
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$XtuDir,
    [string]$Out = ''
)
$ErrorActionPreference = 'Stop'

$handler = [ResolveEventHandler] {
    param($s, $e)
    $name = (New-Object System.Reflection.AssemblyName($e.Name)).Name
    $f = Join-Path $XtuDir ($name + '.dll')
    if (Test-Path $f) { return [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($f) }
    try { return [System.Reflection.Assembly]::ReflectionOnlyLoad($e.Name) } catch { return $null }
}
[AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve($handler)

$iface = [System.Reflection.Assembly]::ReflectionOnlyLoadFrom((Join-Path $XtuDir 'XtuInterface.dll'))
$common = [System.Reflection.Assembly]::ReflectionOnlyLoadFrom((Join-Path $XtuDir 'XtuCommon.dll'))

$lines = New-Object System.Collections.Generic.List[string]
function W([string]$s) { $lines.Add($s) }

function Fmt-Type([Type]$t) {
    if ($t.IsGenericType) {
        $n = $t.GetGenericTypeDefinition().FullName -replace '`\d+', ''
        return $n + '<' + (($t.GetGenericArguments() | ForEach-Object { Fmt-Type $_ }) -join ', ') + '>'
    }
    if ($t.IsByRef) { return (Fmt-Type $t.GetElementType()) + '&' }
    if ($t.IsArray) { return (Fmt-Type $t.GetElementType()) + '[]' }
    return $t.FullName
}
function Fmt-Attrs($data) {
    foreach ($a in $data) {
        $args = @()
        foreach ($c in $a.ConstructorArguments) { $args += ('{0}' -f $c.Value) }
        foreach ($n in $a.NamedArguments) { $args += ('{0}={1}' -f $n.MemberName, $n.TypedValue.Value) }
        '[' + ($a.Constructor.DeclaringType.Name -replace 'Attribute$', '') + '(' + ($args -join ', ') + ')]'
    }
}
$all = [System.Reflection.BindingFlags]'Public,NonPublic,Instance,Static,DeclaredOnly'

function Dump-Type([Type]$t) {
    W ''
    W ('=== ' + $t.FullName + '  (' + $(if ($t.IsInterface) { 'interface' } elseif ($t.IsEnum) { 'enum' } elseif ($t.IsValueType) { 'struct' } else { 'class' }) + ', ' + $t.Assembly.GetName().Name + ')')
    if ($t.BaseType -and $t.BaseType.FullName -ne 'System.Object') { W ('base: ' + (Fmt-Type $t.BaseType)) }
    foreach ($i in $t.GetInterfaces()) { W ('implements: ' + (Fmt-Type $i)) }
    foreach ($s in (Fmt-Attrs ([System.Reflection.CustomAttributeData]::GetCustomAttributes($t)))) { W ('type attr: ' + $s) }
    if ($t.IsEnum) {
        foreach ($f in $t.GetFields($all) | Where-Object { $_.IsStatic }) {
            $v = $f.GetRawConstantValue()
            W ('  {0} = {1}  {2}' -f $f.Name, $v, ((Fmt-Attrs ([System.Reflection.CustomAttributeData]::GetCustomAttributes($f))) -join ' '))
        }
        W ('  underlying: ' + $t.GetField('value__').FieldType.Name)
        return
    }
    foreach ($f in $t.GetFields($all)) {
        $vis = if ($f.IsPublic) { 'public' } elseif ($f.IsFamily) { 'protected' } else { 'private' }
        $st = if ($f.IsStatic) { ' static' } else { '' }
        $ni = if ($f.IsNotSerialized) { ' [NonSerialized]' } else { '' }
        W ('  field {0}{1}{2} {3} {4}  {5}' -f $vis, $st, $ni, (Fmt-Type $f.FieldType), $f.Name, ((Fmt-Attrs ([System.Reflection.CustomAttributeData]::GetCustomAttributes($f))) -join ' '))
    }
    foreach ($p in $t.GetProperties($all)) {
        $acc = @(); if ($p.GetGetMethod($true)) { $acc += 'get' }; if ($p.GetSetMethod($true)) { $acc += 'set' }
        $st = if (($p.GetGetMethod($true) -and $p.GetGetMethod($true).IsStatic)) { 'static ' } else { '' }
        W ('  prop {0}{1} {2} {{{3}}}  {4}' -f $st, (Fmt-Type $p.PropertyType), $p.Name, ($acc -join ';'), ((Fmt-Attrs ([System.Reflection.CustomAttributeData]::GetCustomAttributes($p))) -join ' '))
    }
    foreach ($m in $t.GetMethods($all)) {
        if ($m.IsSpecialName) { continue }
        $ps = ($m.GetParameters() | ForEach-Object {
            $d = ''; if ($_.IsOut) { $d = 'out ' }
            $a = (Fmt-Attrs ([System.Reflection.CustomAttributeData]::GetCustomAttributes($_))) -join ' '
            ($d + (Fmt-Type $_.ParameterType) + ' ' + $_.Name + $(if ($a) { ' ' + $a } else { '' }))
        }) -join ', '
        W ('  method {0} {1}({2})  {3}' -f (Fmt-Type $m.ReturnType), $m.Name, $ps, ((Fmt-Attrs ([System.Reflection.CustomAttributeData]::GetCustomAttributes($m))) -join ' '))
    }
    foreach ($c in $t.GetConstructors($all)) {
        W ('  ctor({0})  {1}' -f (($c.GetParameters() | ForEach-Object { (Fmt-Type $_.ParameterType) + ' ' + $_.Name }) -join ', '), ((Fmt-Attrs ([System.Reflection.CustomAttributeData]::GetCustomAttributes($c))) -join ' '))
    }
}

W ('XTU interface assembly: ' + $iface.FullName)
W ('XTU common assembly:   ' + $common.FullName)
foreach ($asm in $iface, $common) {
    foreach ($s in (Fmt-Attrs ([System.Reflection.CustomAttributeData]::GetCustomAttributes($asm)))) { W ('assembly attr: ' + $s) }
}
$want = 'IronCity.Core.IXtuTuning', 'IronCity.Core.IXtuMonitoring', 'IronCity.Core.XtuTuningControl', 'IronCity.Core.XtuTuningProposal',
        'IronCity.Common.SdkClientToken', 'IronCity.Common.TuningResult', 'IronCity.Common.TuningErrorCode'
foreach ($n in $want) {
    $t = $iface.GetType($n, $false); if (-not $t) { $t = $common.GetType($n, $false) }
    if ($t) { Dump-Type $t } else { W ''; W ("=== NOT FOUND: $n") }
}
# Types the above refer to (element types of the lists and dictionaries), found by name in either assembly.
$seen = New-Object System.Collections.Generic.HashSet[string]
foreach ($n in $want) { [void]$seen.Add($n) }
$more = New-Object System.Collections.Generic.Queue[Type]
foreach ($n in $want) {
    $t = $iface.GetType($n, $false); if (-not $t) { $t = $common.GetType($n, $false) }
    if (-not $t) { continue }
    $refs = @()
    foreach ($m in $t.GetMethods($all)) { $refs += $m.ReturnType; $refs += ($m.GetParameters() | ForEach-Object { $_.ParameterType }) }
    foreach ($p in $t.GetProperties($all)) { $refs += $p.PropertyType }
    foreach ($f in $t.GetFields($all)) { $refs += $f.FieldType }
    foreach ($r in $refs) {
        $stack = New-Object System.Collections.Generic.Stack[Type]; $stack.Push($r)
        while ($stack.Count) {
            $x = $stack.Pop()
            if ($x.IsByRef -or $x.IsArray) { $stack.Push($x.GetElementType()); continue }
            if ($x.IsGenericType) { foreach ($g in $x.GetGenericArguments()) { $stack.Push($g) } }
            if ($x.Assembly.GetName().Name -in 'XtuInterface', 'XtuCommon' -and $seen.Add($x.FullName)) { $more.Enqueue($x) }
        }
    }
}
W ''; W '##### Types referenced by the above, in the same two assemblies #####'
while ($more.Count) { Dump-Type $more.Dequeue() }

if ($Out) { $lines | Set-Content -Path $Out -Encoding UTF8; "written: $Out ($($lines.Count) lines)" } else { $lines }
