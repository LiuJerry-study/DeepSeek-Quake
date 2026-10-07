<#
  DeepSeek Quake —— 配置文件回写（由小工具调用）

  为什么要有这个脚本：
    本机火绒会按程序拦截「未签名程序写文件」。DeepSeekQuake.exe 没有签名，
    所以它自己绝不写文件；需要改配置时，它调用这个脚本（powershell 是签名的）
    来写。这样既满足了杀软，又能让设置窗口里改的东西真正落到 hotkey.conf。

  用法（主接口 = -Conf + 重复的 -Set）：
    pwsh -NoProfile -ExecutionPolicy Bypass -File config-write.ps1 `
         -Conf "D:\...\hotkey.conf" `
         -Set "hotkey=CTRL+ALT+SPACE" -Set "rightclickpaste=1" -Set "browser=auto"

  也兼容这几种写法：
    -Set "hotkey=A" -Set "browser=edge"        重复 -Set（主接口，工具内部就用这个）
    -Sets "hotkey=A" -Updates "browser=edge"   -Sets / -Updates 是 -Set 的别名
    -Set "hotkey=A,browser=edge"               逗号分隔（值里没有英文逗号时可用）
    -Set "hotkey=A||browser=edge"              || 分隔（值里含逗号/空格/& 时用这个）

  已知限制：每个键值对都必须跟着 -Set / -Sets / -Updates 之一，不能写成
  `-Conf x.conf "hotkey=A" "browser=edge"` 这种裸值。PowerShell 的参数绑定会把
  第一个裸值抢去填位置参数 -Restart，绑定阶段就报错或把值当路径用——这是
  PowerShell 的绑定规则，脚本内部绕不开。上面的四种写法都实测可用。

  为什么不用 [string[]]$Set 直接接重复参数：
    PowerShell 里 -Set 是 Set-Variable 的内置别名，而且脚本参数重复指定时不会
    累加成数组，会直接报「多次指定了参数」。所以这里用 ValueFromRemainingArguments
    把 -Set / -Sets / -Updates 后面的 token 全部收进来自己解析。

  参数：
    -Conf      要改的配置文件（不存在就新建）
    -Restart   可选：写完配置后重启小工具（改 url / 浏览器 / 尺寸 时用）
    -RestartDelayMs  重启前等待毫秒数，默认 700，给小工具留出退出时间

  行为约定：
    * 只改指定键的值；注释、空行、原有顺序全部保留
    * 键不存在就追加到文件末尾
    * 同一个键在文件里出现多次时，只改第一条，其余重复项注释掉
    * UTF-8 无 BOM + CRLF，中文注释不变
    * 原子写入（临时文件 + 覆盖式改名），中途失败不会留下半个配置文件
    * 成功打印 OK，失败打印 FAIL: 原因 并以非 0 退出
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Conf,
    [string]$Restart = '',
    [int]$RestartDelayMs = 700,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest = @()
)

$ErrorActionPreference = 'Stop'

function Fail([string]$msg) { Write-Output "FAIL: $msg"; exit 1 }

function Strip-Quotes([string]$s) {
    if ([string]::IsNullOrEmpty($s)) { return $s }
    if ($s.Length -ge 2) {
        if (($s[0] -eq '"' -and $s[$s.Length - 1] -eq '"') -or
            ($s[0] -eq "'" -and $s[$s.Length - 1] -eq "'")) {
            return $s.Substring(1, $s.Length - 2)
        }
    }
    return $s
}

try {
    if ([string]::IsNullOrWhiteSpace($Conf)) { Fail '缺少 -Conf' }
    $full = [System.IO.Path]::GetFullPath((Strip-Quotes $Conf))

    # ── 把 -Set / -Sets / -Updates / 位置参数 全部摊平成 "key=value" 片段 ──
    $chunks = New-Object System.Collections.Generic.List[string]
    $i = 0
    while ($i -lt $Rest.Count) {
        $tok = $Rest[$i]
        if ([string]::IsNullOrWhiteSpace($tok)) { $i++; continue }

        # 位置参数形式下，键值对之间可能夹着一个裸的 key=value（没有 -Set 前缀），
        # 也可能夹着 5；这里把纯数字当成「多余的参数」直接跳过，避免它被
        # PowerShell 误绑到 -RestartDelayMs 上。
        if ($tok -match '^\d+$') { $i++; continue }

        $isFlag = $false
        foreach ($flag in @('-Set', '-Sets', '-Updates', '--Set', '--Sets', '--Updates')) {
            if ($tok -ieq $flag) { $isFlag = $true; break }
        }
        if ($isFlag) {
            $i++
            if ($i -lt $Rest.Count) { $chunks.Add($Rest[$i]) }
            $i++
            continue
        }
        $chunks.Add($tok)
        $i++
    }

    $pairs = @()
    foreach ($chunk in $chunks) {
        $c = Strip-Quotes $chunk
        foreach ($one in ($c -split '\|\|')) {
            if ([string]::IsNullOrWhiteSpace($one)) { continue }
            # 允许逗号分隔多对；只有当逗号后面确实跟着 "key=" 形式时才拆
            $pieces = @($one)
            if ($one.Contains(',') -and $one -match ',[^=,]+=') { $pieces = $one -split ',' }
            foreach ($piece in $pieces) {
                if ([string]::IsNullOrWhiteSpace($piece)) { continue }
                $eq = $piece.IndexOf('=')
                if ($eq -le 0) { Fail "非法的更新项（缺少 =）: $piece" }
                $pairs += , @{ Key = $piece.Substring(0, $eq).Trim(); Value = $piece.Substring($eq + 1) }
            }
        }
    }

    if ($pairs.Count -eq 0) { Fail '没有要写入的项（没给 -Set）' }

    # ── 读入 ──
    if (Test-Path -LiteralPath $full) {
        $lines = [System.IO.File]::ReadAllLines($full, [System.Text.Encoding]::UTF8)
    } else {
        $lines = @()
    }

    $want = @{}
    foreach ($p in $pairs) { $want[$p.Key.ToLowerInvariant()] = $p.Value }

    $out = New-Object System.Collections.Generic.List[string]
    $written = @{}
    foreach ($line in $lines) {
        $m = [regex]::Match($line, '^\s*([A-Za-z0-9_.\-]+)\s*=(.*)$')
        if ($m.Success) {
            $k = $m.Groups[1].Value
            $kl = $k.ToLowerInvariant()
            if ($want.ContainsKey($kl)) {
                if ($written.ContainsKey($kl)) {
                    $out.Add("# (重复项，已由 config-write.ps1 注释) $line")
                } else {
                    $out.Add("$k=$($want[$kl])")
                    $written[$kl] = $true
                }
                continue
            }
        }
        $out.Add($line)
    }

    # 文件里没有的键追加到末尾
    foreach ($p in $pairs) {
        $kl = $p.Key.ToLowerInvariant()
        if (-not $written.ContainsKey($kl)) {
            $out.Add("$($p.Key)=$($p.Value)")
            $written[$kl] = $true
        }
    }

    $text = ($out -join "`r`n")
    if ($out.Count -gt 0) { $text += "`r`n" }

    $enc = New-Object System.Text.UTF8Encoding($false)      # 无 BOM
    $dir = [System.IO.Path]::GetDirectoryName($full)
    if ([string]::IsNullOrEmpty($dir)) { $dir = (Get-Location).Path }
    if (-not (Test-Path -LiteralPath $dir)) { [System.IO.Directory]::CreateDirectory($dir) | Out-Null }

    $tmp = Join-Path $dir ('.' + [System.IO.Path]::GetFileName($full) + '.' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.tmp')
    [System.IO.File]::WriteAllText($tmp, $text, $enc)

    # 覆盖式改名：原子操作。
    # 注意：不要用 [System.IO.File]::Replace($tmp,$full,$null) —— PowerShell 会把
    # $null 变成空字符串传给第三个参数，.NET 直接抛 "The path is empty"。
    if (Test-Path -LiteralPath $full) {
        [System.IO.File]::Move($tmp, $full, $true)
    } else {
        [System.IO.File]::Move($tmp, $full)
    }

    Write-Output ("OK: updated {0} key(s) in {1}" -f $written.Count, $full)
}
catch {
    if ($tmp -and (Test-Path -LiteralPath $tmp)) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    Fail $_.Exception.Message
}

if (-not [string]::IsNullOrWhiteSpace($Restart)) {
    try {
        if ($RestartDelayMs -gt 0) { Start-Sleep -Milliseconds $RestartDelayMs }
        $exe = Strip-Quotes $Restart
        $rdir = [System.IO.Path]::GetDirectoryName($exe)
        if ([string]::IsNullOrEmpty($rdir)) { $rdir = (Get-Location).Path }
        Start-Process -FilePath $exe -WorkingDirectory $rdir -ArgumentList '--restarted'
        Write-Output "OK: restarted $exe"
    } catch {
        Write-Output "FAIL: restart failed: $($_.Exception.Message)"
        exit 1
    }
}

exit 0
