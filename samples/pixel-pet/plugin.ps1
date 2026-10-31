#requires -Version 5.1
$ErrorActionPreference = 'Stop'

# Keep the source UTF-8 BOM for Windows PowerShell 5.1. Protocol streams have no BOM.
$utf8 = [System.Text.UTF8Encoding]::new($false)
$reader = [System.IO.StreamReader]::new([Console]::OpenStandardInput(), $utf8, $false)
$writer = [System.IO.StreamWriter]::new([Console]::OpenStandardOutput(), $utf8)
$writer.AutoFlush = $true

$themes = @(
    @{ Name = '薄荷'; Colors = @('#00000000', '#BEDDC9', '#23392E', '#F4FFF8') },
    @{ Name = '晴蓝'; Colors = @('#00000000', '#B5D5F0', '#233549', '#F4FAFF') },
    @{ Name = '暖杏'; Colors = @('#00000000', '#EDCBA9', '#463526', '#FFF9F1') }
)
$script:themeIndex = 0
$base = @(
    '0000000000000000',
    '0000110000110000',
    '0001110000111000',
    '0001111111111000',
    '0011111111111100',
    '0011121111211100',
    '0011121111211100',
    '0011111131111100',
    '0001111111111000',
    '0000111111110000',
    '0000011111100000',
    '0000111111110000',
    '0001111111111000',
    '0001110000111000',
    '0001100000011000',
    '0000000000000000'
)
$blink = @($base.Clone())
$blink[5] = '0011111111111100'
$blink[6] = '0011221111221100'
$step = @($base.Clone())
$step[13] = '0000111001110000'
$step[14] = '0000011001100000'
$frames = @(($base -join ''), ($blink -join ''), ($base -join ''), ($step -join ''))

function Send-Message($Message) {
    $writer.WriteLine(($Message | ConvertTo-Json -Depth 12 -Compress))
}

function New-Sprite([int]$Size) {
    return @{
        type = 'sprite'; pixelWidth = 16; pixelHeight = 16
        palette = @($themes[$script:themeIndex].Colors); frames = $frames; fps = 4
        width = $Size; height = $Size
    }
}

function Send-Views {
    $theme = $themes[$script:themeIndex]
    Send-Message @{
        type = 'view'; id = 'avatar'; slot = 'idle.widget'
        placement = @{ defaultSide = 'left'; allowedSides = @('left', 'right') }
        root = (New-Sprite 28)
    }
    Send-Message @{
        type = 'view'; id = 'pet'; slot = 'activity.tab'; title = '像素伙伴'
        root = @{
            type = 'column'; width = 320; height = 200; padding = 12; spacing = 8
            children = @(
                @{ type = 'text'; text = '像素伙伴'; fontSize = 20; weight = 'semibold' },
                @{
                    type = 'row'; spacing = 18; align = 'center'
                    children = @(
                        (New-Sprite 32),
                        @{
                            type = 'ring'; width = 48; height = 48; thickness = 3
                            value = [Math]::Round(($script:themeIndex + 1) / 3.0, 4)
                            text = ('{0} / 3' -f ($script:themeIndex + 1))
                            color = $theme.Colors[1]; trackColor = '#25252B'
                        }
                    )
                },
                @{ type = 'text'; text = ('配色：{0}' -f $theme.Name); color = '#B8B8C4'; fontSize = 15 },
                @{ type = 'button'; text = '切换配色'; actionId = 'next'; height = 36; align = 'stretch'; background = '#25252B'; cornerRadius = 12 }
            )
        }
    }
}

$ready = $false
try {
    # The host animates the supplied frames. The provider sleeps inside ReadLine between messages.
    while ($null -ne ($line = $reader.ReadLine())) {
        $message = $line | ConvertFrom-Json
        if ($message.type -eq 'shutdown') { break }
        if ($message.type -eq 'hello') {
            if ($message.protocolVersion -ne 1) { throw 'Unsupported protocol version.' }
            if (-not $ready) {
                Send-Message @{ type = 'hello'; protocolVersion = 1 }
                $ready = $true
                Send-Views
            }
        }
        elseif ($ready -and $message.type -eq 'viewAction' -and $message.viewId -eq 'pet' -and $message.actionId -eq 'next') {
            $script:themeIndex = ($script:themeIndex + 1) % $themes.Count
            Send-Views
        }
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
