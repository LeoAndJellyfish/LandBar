#requires -Version 5.1
$ErrorActionPreference = 'Stop'

# The UTF-8 BOM on this source file is required for Chinese strings in Windows PowerShell 5.1.
# Protocol streams use UTF-8 without a BOM; stdout contains JSON Lines only.
$utf8 = [System.Text.UTF8Encoding]::new($false)
$reader = [System.IO.StreamReader]::new([Console]::OpenStandardInput(), $utf8, $false)
$writer = [System.IO.StreamWriter]::new([Console]::OpenStandardOutput(), $utf8)
$writer.AutoFlush = $true

function Send-Message($Message) {
    $writer.WriteLine(($Message | ConvertTo-Json -Depth 6 -Compress))
}

$clock = [System.Diagnostics.Stopwatch]::StartNew()
$previous = $clock.Elapsed.TotalSeconds
$nextRefresh = 0.0
$total = 1500.0
$remaining = $total
$paused = $false
$completionSent = $false
$ready = $false
$dirty = $true
$inputTask = $reader.ReadLineAsync()

try {
    while ($true) {
        $now = $clock.Elapsed.TotalSeconds
        $delta = $now - $previous
        $previous = $now
        if ($ready -and -not $paused -and $remaining -gt 0) {
            # Explicit double arguments avoid PowerShell 5.1 selecting the integer overload.
            $remaining = [Math]::Max([double]0, [double]($remaining - $delta))
        }

        if ($inputTask.IsCompleted) {
            $line = $inputTask.GetAwaiter().GetResult()
            if ($null -eq $line) { break }
            $message = $line | ConvertFrom-Json
            if ($message.type -eq 'shutdown') { break }
            if ($message.type -eq 'hello') {
                if ($message.protocolVersion -ne 1) { throw 'Unsupported protocol version.' }
                if (-not $ready) {
                    Send-Message @{ type = 'hello'; protocolVersion = 1 }
                    $ready = $true
                    $dirty = $true
                }
            }
            elseif ($ready -and $message.type -eq 'action' -and $message.cardId -eq 'focus') {
                switch ($message.actionId) {
                    'toggle' {
                        if ($remaining -le 0) {
                            $remaining = $total
                            $paused = $false
                            $completionSent = $false
                            Send-Message @{ type = 'remove'; id = 'completed' }
                        }
                        else { $paused = -not $paused }
                        $dirty = $true
                    }
                    'reset' {
                        $total = 1500.0
                        $remaining = $total
                        $paused = $false
                        $completionSent = $false
                        Send-Message @{ type = 'remove'; id = 'completed' }
                        $dirty = $true
                    }
                    'demo' {
                        $total = 10.0
                        $remaining = $total
                        $paused = $false
                        $completionSent = $false
                        Send-Message @{ type = 'remove'; id = 'completed' }
                        $dirty = $true
                    }
                }
            }
            $inputTask = $reader.ReadLineAsync()
        }

        if ($ready -and $remaining -le 0 -and -not $completionSent) {
            Send-Message @{
                type = 'upsert'
                id = 'completed'
                title = '本轮完成'
                body = '休息片刻，再开始下一轮。'
                icon = 'check'
                ttlSeconds = 10
            }
            $completionSent = $true
            $dirty = $true
        }

        if ($ready -and ($dirty -or $now -ge $nextRefresh)) {
            $seconds = [int][Math]::Ceiling($remaining)
            $minutes = [int][Math]::Floor($seconds / 60)
            $time = '{0:00}:{1:00}' -f $minutes, ($seconds % 60)
            $state = if ($remaining -le 0) { '本轮完成' } elseif ($paused) { '已暂停' } else { '进行中' }
            $toggleLabel = if ($remaining -le 0) { '再来一轮' } elseif ($paused) { '继续' } else { '暂停' }
            Send-Message @{
                type = 'upsert'
                id = 'focus'
                title = '专注计时'
                body = "$time · $state"
                icon = 'timer'
                progress = [Math]::Round(1.0 - $remaining / $total, 4)
                actions = @(
                    @{ id = 'toggle'; label = $toggleLabel },
                    @{ id = 'reset'; label = '重置 25 分钟' },
                    @{ id = 'demo'; label = '演示 10 秒' }
                )
            }
            $dirty = $false
            $nextRefresh = $now + 1.0
        }
        Start-Sleep -Milliseconds 100
    }
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
finally {
    $writer.Dispose()
    $reader.Dispose()
}
