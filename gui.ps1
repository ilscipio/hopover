# Hopover wizard. Started elevated by Hopover.cmd. Runs hopover.ps1 as a child process
# and reads its output file: plain lines go to the log, "##" lines drive the screens.
param([string]$SessionUser)

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

Add-Type -TypeDefinition @'
using System.ComponentModel;
public class PlanRow : INotifyPropertyChanged {
    private bool include = true;
    public bool Include { get { return include; } set { include = value; Changed("Include"); } }
    public string Path { get; set; }
    public string Why { get; set; }
    public long Bytes { get; set; }
    public long Files { get; set; }
    public string Size { get { return Bytes >= 1073741824 ? (Bytes / 1073741824.0).ToString("0.0") + " GB" : (Bytes / 1048576.0).ToString("0") + " MB"; } }
    public event PropertyChangedEventHandler PropertyChanged;
    void Changed(string n) { if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(n)); }
}
'@

# Continue, not Stop: in PS 5.1 any stderr line of net.exe or robocopy would throw.
$ErrorActionPreference = 'Continue'
# Tells the engine it has no console: it must fail instead of prompting.
$env:HOPOVER_GUI = '1'
$Root = $PSScriptRoot
$Engine = Join-Path $Root 'hopover.ps1'
$Store = 'C:\hopover-store'
. (Join-Path $Root 'lib\Common.ps1')

$StepNames = [ordered]@{
    apps     = 'Apps (winget)'
    env      = 'Environment variables and PATH'
    files    = 'Files: profile, .ssh, dot-folders, C:\ folders, other drives'
    settings = 'Settings, Wi-Fi, mapped drives, printers'
    certs    = 'Personal certificates'
    wsl      = 'WSL distros'
    docker   = 'Docker named volumes'
    tasks    = 'Scheduled tasks (arrive switched off)'
}
$ExportSteps = 'apps', 'env', 'settings', 'certs', 'tasks', 'wsl', 'docker'
$ImportSteps = 'apps', 'env', 'files', 'settings', 'certs', 'wsl', 'docker', 'tasks'

[xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Hopover" Width="1000" Height="720" MinWidth="820" MinHeight="600"
        WindowStartupLocation="CenterScreen" FontFamily="Segoe UI" FontSize="14" Background="#FAFAF9">
  <Window.Resources>
    <Style TargetType="Button">
      <Setter Property="Padding" Value="18,9"/>
      <Setter Property="Margin" Value="0,0,10,0"/>
      <Setter Property="Background" Value="#FFFFFF"/>
      <Setter Property="BorderBrush" Value="#D6D3D1"/>
      <Setter Property="Cursor" Value="Hand"/>
    </Style>
    <Style x:Key="Primary" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
      <Setter Property="Background" Value="#1F6FEB"/>
      <Setter Property="Foreground" Value="White"/>
      <Setter Property="BorderBrush" Value="#1F6FEB"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
    </Style>
    <Style x:Key="Big" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
      <Setter Property="Padding" Value="24,22"/>
      <Setter Property="Margin" Value="0,0,0,14"/>
      <Setter Property="HorizontalContentAlignment" Value="Left"/>
    </Style>
    <Style x:Key="H1" TargetType="TextBlock">
      <Setter Property="FontSize" Value="26"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
      <Setter Property="Foreground" Value="#1C1917"/>
      <Setter Property="Margin" Value="0,0,0,6"/>
    </Style>
    <Style x:Key="Sub" TargetType="TextBlock">
      <Setter Property="Foreground" Value="#57534E"/>
      <Setter Property="TextWrapping" Value="Wrap"/>
      <Setter Property="Margin" Value="0,0,0,18"/>
    </Style>
    <Style TargetType="CheckBox">
      <Setter Property="Margin" Value="0,0,0,8"/>
      <Setter Property="VerticalContentAlignment" Value="Center"/>
    </Style>
  </Window.Resources>

  <DockPanel Margin="32,24,32,24">
    <TextBlock DockPanel.Dock="Top" Margin="0,0,0,20" FontSize="15" Foreground="#78716C">
      <Run Text="Hopover" FontWeight="Bold" Foreground="#1F6FEB"/><Run Text="  ·  move this PC to a new one"/>
    </TextBlock>

    <Grid>
      <!-- Start -->
      <StackPanel x:Name="PageStart" MaxWidth="620" HorizontalAlignment="Left">
        <TextBlock Style="{StaticResource H1}" Text="Which PC is this?"/>
        <TextBlock Style="{StaticResource Sub}" Text="Run Hopover on both PCs. Both stay on and in the same network. The old PC only shares; nothing on it changes."/>
        <Button x:Name="BtnOld" Style="{StaticResource Big}">
          <StackPanel>
            <TextBlock Text="This is the OLD PC" FontSize="18" FontWeight="SemiBold"/>
            <TextBlock Text="Scan it, choose what moves, and share it with the new PC." Foreground="#57534E"/>
          </StackPanel>
        </Button>
        <Button x:Name="BtnNew" Style="{StaticResource Big}">
          <StackPanel>
            <TextBlock Text="This is the NEW PC" FontSize="18" FontWeight="SemiBold"/>
            <TextBlock Text="Connect to the old PC and bring everything over." Foreground="#57534E"/>
          </StackPanel>
        </Button>
        <TextBlock x:Name="ResumeNote" Visibility="Collapsed" Foreground="#B45309" TextWrapping="Wrap"
                   Text="A move on this PC did not finish. Choose NEW PC: the done steps are skipped and the file copy goes on where it stopped."/>
      </StackPanel>

      <!-- Old PC: scan and choose -->
      <DockPanel x:Name="PageScan" Visibility="Collapsed">
        <StackPanel DockPanel.Dock="Top">
          <TextBlock Style="{StaticResource H1}" Text="What moves to the new PC"/>
          <TextBlock x:Name="ScanSub" Style="{StaticResource Sub}" Text="Hopover measures every folder first. This takes some minutes on a big disk."/>
          <ProgressBar x:Name="ScanBar" Height="6" Margin="0,0,0,6" Maximum="1"/>
          <TextBlock x:Name="ScanText" Foreground="#78716C" TextTrimming="CharacterEllipsis" Margin="0,0,0,12"/>
        </StackPanel>
        <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" Margin="0,16,0,0">
          <Button x:Name="BtnExport" Style="{StaticResource Primary}" Content="Prepare the move" IsEnabled="False"/>
          <TextBlock x:Name="TotalText" Margin="24,8,0,0" FontWeight="SemiBold"/>
        </StackPanel>
        <Grid>
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="300"/>
          </Grid.ColumnDefinitions>
          <DataGrid x:Name="PlanGrid" AutoGenerateColumns="False" CanUserAddRows="False" HeadersVisibility="Column"
                    GridLinesVisibility="None" Background="White" BorderBrush="#E7E5E4" RowHeight="28" IsEnabled="False">
            <DataGrid.Columns>
              <DataGridTemplateColumn Header="Move" Width="56">
                <DataGridTemplateColumn.CellTemplate>
                  <DataTemplate>
                    <CheckBox IsChecked="{Binding Include, UpdateSourceTrigger=PropertyChanged}" HorizontalAlignment="Center" Margin="0"/>
                  </DataTemplate>
                </DataGridTemplateColumn.CellTemplate>
              </DataGridTemplateColumn>
              <DataGridTextColumn Header="Size" Binding="{Binding Size}" Width="80" IsReadOnly="True" SortMemberPath="Bytes"/>
              <DataGridTextColumn Header="Files" Binding="{Binding Files, StringFormat=N0}" Width="90" IsReadOnly="True"/>
              <DataGridTextColumn Header="Kind" Binding="{Binding Why}" Width="120" IsReadOnly="True"/>
              <DataGridTextColumn Header="Path" Binding="{Binding Path}" Width="*" IsReadOnly="True"/>
            </DataGrid.Columns>
          </DataGrid>
          <StackPanel Grid.Column="1" Margin="24,0,0,0">
            <TextBlock Text="Also move" FontWeight="SemiBold" Margin="0,0,0,10"/>
            <StackPanel x:Name="OldSteps"/>
            <StackPanel x:Name="OldCertPanel" Visibility="Collapsed" Margin="0,8,0,0">
              <TextBlock Text="Password for the certificates" Foreground="#57534E" Margin="0,0,0,4"/>
              <PasswordBox x:Name="OldCertPw" Padding="6"/>
              <TextBlock Text="You type it again on the new PC." Foreground="#78716C" FontSize="12"/>
            </StackPanel>
            <TextBlock Style="{StaticResource Sub}" Margin="0,16,0,0" FontSize="12"
                       Text="OneDrive and SeaDrive folders are left out: they sync after you sign in on the new PC. 1Password lives in your account: you only sign in."/>
          </StackPanel>
        </Grid>
      </DockPanel>

      <!-- Run (both PCs) -->
      <DockPanel x:Name="PageRun" Visibility="Collapsed">
        <StackPanel DockPanel.Dock="Top">
          <TextBlock x:Name="RunTitle" Style="{StaticResource H1}"/>
          <TextBlock x:Name="RunSub" Style="{StaticResource Sub}"/>
        </StackPanel>
        <Border x:Name="SharePanel" DockPanel.Dock="Top" Visibility="Collapsed" Background="#ECFDF5" BorderBrush="#A7F3D0"
                BorderThickness="1" CornerRadius="6" Padding="18" Margin="0,0,0,16">
          <StackPanel>
            <TextBlock Text="Ready. Now go to the new PC." FontSize="18" FontWeight="SemiBold" Foreground="#065F46"/>
            <TextBlock x:Name="ShareText" TextWrapping="Wrap" Margin="0,8,0,10" Foreground="#064E3B"/>
            <StackPanel Orientation="Horizontal">
              <Button x:Name="BtnCleanup" Content="Stop sharing (after the move)"/>
            </StackPanel>
          </StackPanel>
        </Border>
        <StackPanel x:Name="CopyPanel" DockPanel.Dock="Top" Visibility="Collapsed" Margin="0,0,0,14">
          <ProgressBar x:Name="CopyBar" Height="10" Maximum="1"/>
          <TextBlock x:Name="CopyText" Margin="0,6,0,0" Foreground="#44403C"/>
          <TextBlock x:Name="CopyItem" Foreground="#78716C" TextTrimming="CharacterEllipsis"/>
        </StackPanel>
        <Border x:Name="DonePanel" DockPanel.Dock="Bottom" Visibility="Collapsed" Margin="0,16,0,0">
          <WrapPanel>
            <Button x:Name="BtnChecklist" Style="{StaticResource Primary}" Content="Open the checklist"/>
            <Button x:Name="BtnTasks" Content="Turn on scheduled tasks"/>
            <Button x:Name="BtnVerify" Content="Check again"/>
            <Button x:Name="BtnLogs" Content="Open log folder"/>
          </WrapPanel>
        </Border>
        <Grid>
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width="330"/>
            <ColumnDefinition Width="*"/>
          </Grid.ColumnDefinitions>
          <StackPanel x:Name="RunSteps" Margin="0,0,20,0"/>
          <TextBox x:Name="LogBox" Grid.Column="1" IsReadOnly="True" FontFamily="Cascadia Mono, Consolas" FontSize="12"
                   VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Auto" Background="#1C1917" Foreground="#E7E5E4"
                   BorderThickness="0" Padding="10"/>
        </Grid>
      </DockPanel>

      <!-- New PC: connect -->
      <StackPanel x:Name="PageConnect" Visibility="Collapsed" MaxWidth="760" HorizontalAlignment="Left">
        <TextBlock Style="{StaticResource H1}" Text="Connect to the old PC"/>
        <TextBlock Style="{StaticResource Sub}" Text="The old PC shows its name, IP and user in green after 'Prepare the move'. The password is the Windows password of that user on the old PC (with a Microsoft account: its password, not the PIN)."/>
        <Grid>
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width="200"/>
            <ColumnDefinition Width="*"/>
          </Grid.ColumnDefinitions>
          <Grid.RowDefinitions>
            <RowDefinition/><RowDefinition/><RowDefinition/>
          </Grid.RowDefinitions>
          <TextBlock Text="Old PC name or IP" VerticalAlignment="Center"/>
          <TextBox x:Name="FromBox" Grid.Column="1" Padding="6" Margin="0,0,0,10"/>
          <TextBlock Grid.Row="1" Text="User on the old PC" VerticalAlignment="Center"/>
          <TextBox x:Name="UserBox" Grid.Row="1" Grid.Column="1" Padding="6" Margin="0,0,0,10"/>
          <TextBlock Grid.Row="2" Text="Password" VerticalAlignment="Center"/>
          <PasswordBox x:Name="PwBox" Grid.Row="2" Grid.Column="1" Padding="6" Margin="0,0,0,10"/>
        </Grid>
        <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
          <Button x:Name="BtnConnect" Style="{StaticResource Primary}" Content="Connect"/>
          <TextBlock x:Name="ConnectText" VerticalAlignment="Center" TextWrapping="Wrap" MaxWidth="520"/>
        </StackPanel>
        <Border x:Name="ImportPanel" Visibility="Collapsed" Margin="0,22,0,0" Background="White" BorderBrush="#E7E5E4" BorderThickness="1" CornerRadius="6" Padding="18">
          <StackPanel>
            <TextBlock x:Name="SummaryText" TextWrapping="Wrap" Margin="0,0,0,14" FontWeight="SemiBold"/>
            <StackPanel x:Name="NewSteps"/>
            <StackPanel x:Name="NewCertPanel" Visibility="Collapsed" Margin="0,4,0,8" Orientation="Horizontal">
              <TextBlock Text="Certificate password" VerticalAlignment="Center" Margin="0,0,10,0"/>
              <PasswordBox x:Name="NewCertPw" Width="240" Padding="6"/>
            </StackPanel>
            <StackPanel Orientation="Horizontal" Margin="0,10,0,0">
              <Button x:Name="BtnImport" Style="{StaticResource Primary}" Content="Start the move"/>
              <CheckBox x:Name="DryNew" Content="Dry run (change nothing)" Margin="12,8,0,0"/>
            </StackPanel>
          </StackPanel>
        </Border>
      </StackPanel>
    </Grid>
  </DockPanel>
</Window>
'@

$reader = New-Object System.Xml.XmlNodeReader $xaml
$Window = [Windows.Markup.XamlReader]::Load($reader)
$ui = @{}
$xaml.SelectNodes('//*[@*[local-name()="Name"]]') | ForEach-Object {
    $n = $_.Attributes | Where-Object { $_.LocalName -eq 'Name' } | Select-Object -First 1
    $ui[$n.Value] = $Window.FindName($n.Value)
}

$Pages = 'PageStart', 'PageScan', 'PageRun', 'PageConnect'
function Show-Page([string]$Name) {
    foreach ($p in $Pages) { $ui[$p].Visibility = if ($p -eq $Name) { 'Visible' } else { 'Collapsed' } }
}

function Format-Size([double]$Bytes) {
    if ($Bytes -ge 1GB) { '{0:N1} GB' -f ($Bytes / 1GB) } else { '{0:N0} MB' -f ($Bytes / 1MB) }
}

function New-StepBoxes($Panel, [string[]]$Steps, [string[]]$Done) {
    $Panel.Children.Clear()
    $boxes = [ordered]@{}
    foreach ($s in $Steps) {
        $cb = New-Object System.Windows.Controls.CheckBox
        $cb.Content = $StepNames[$s] + $(if ($Done -contains $s) { '  (done)' } else { '' })
        $cb.IsChecked = -not ($Done -contains $s)
        $cb.Tag = $s
        [void]$Panel.Children.Add($cb)
        $boxes[$s] = $cb
    }
    $boxes
}

function Get-CheckedSteps($Boxes) {
    @($Boxes.Values | Where-Object { $_.IsChecked } | ForEach-Object { $_.Tag })
}

# ---------- engine runner ----------
$script:Proc = $null
$script:OutFile = $null
$script:Pos = 0
$script:Rest = ''
$script:OnEvent = $null
$script:OnExit = $null
$script:LogLen = 0

$Timer = New-Object System.Windows.Threading.DispatcherTimer
$Timer.Interval = [TimeSpan]::FromMilliseconds(300)

function Add-Log([string]$Line) {
    $ui.LogBox.AppendText($Line + "`r`n")
    $script:LogLen += $Line.Length + 2
    if ($script:LogLen -gt 400000) {
        # Keep the box light: the full log is in C:\hopover-store\logs.
        $ui.LogBox.Text = $ui.LogBox.Text.Substring($ui.LogBox.Text.Length - 200000)
        $script:LogLen = $ui.LogBox.Text.Length
    }
    $ui.LogBox.ScrollToEnd()
}

function Read-EngineOutput {
    if (-not $script:OutFile -or -not (Test-Path $script:OutFile)) { return }
    $fs = [IO.File]::Open($script:OutFile, 'Open', 'Read', 'ReadWrite')
    try {
        [void]$fs.Seek($script:Pos, 'Begin')
        $buf = New-Object byte[] ($fs.Length - $script:Pos)
        $n = $fs.Read($buf, 0, $buf.Length)
        $script:Pos += $n
    } finally { $fs.Dispose() }
    if ($n -le 0) { return }
    $text = $script:Rest + [Text.Encoding]::UTF8.GetString($buf, 0, $n).TrimStart([char]0xFEFF)
    $lines = $text -split "`r?`n"
    $script:Rest = $lines[-1]
    # The last piece has no newline yet; it waits in $Rest. 0..-1 would wrap around, so guard.
    if ($lines.Count -lt 2) { return }
    foreach ($l in $lines[0..($lines.Count - 2)]) {
        if ($l.StartsWith('##')) {
            $kind, $data = $l.Substring(2) -split ' ', 2
            if ($script:OnEvent) { & $script:OnEvent $kind $data }
        } elseif ($l.Trim()) { Add-Log $l }
    }
}

$Timer.Add_Tick({
    try {
        Read-EngineOutput
        if ($script:Proc -and $script:Proc.HasExited) {
            Read-EngineOutput
            if ($script:Rest) { Add-Log $script:Rest; $script:Rest = '' }
            $Timer.Stop()
            $code = $script:Proc.ExitCode
            $script:Proc = $null
            if ($script:OnExit) { & $script:OnExit $code }
        }
    } catch { Add-Log "GUI: $($_.Exception.Message)" }
})

function Start-Engine([string[]]$Arguments, [string]$CertPw, [scriptblock]$OnEvent, [scriptblock]$OnExit) {
    $script:OutFile = Join-Path $env:TEMP "hopover-$([DateTime]::Now.Ticks).out"
    $script:Pos = 0; $script:Rest = ''
    $script:OnEvent = $OnEvent; $script:OnExit = $OnExit
    $argLine = "-NoProfile -ExecutionPolicy Bypass -File `"$Engine`" " + (($Arguments | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' ')
    Add-Log "> hopover.ps1 $($Arguments -join ' ')"
    # The certificate password goes to the child through its environment only.
    if ($CertPw) { $env:HOPOVER_CERT_PW = $CertPw }
    try {
        $script:Proc = Start-Process powershell.exe -ArgumentList $argLine -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput $script:OutFile -RedirectStandardError "$($script:OutFile).err"
    } finally { Remove-Item Env:\HOPOVER_CERT_PW -ErrorAction SilentlyContinue }
    $Timer.Start()
}

# ---------- run page: step rows ----------
$script:StepRows = @{}
function New-RunSteps([string[]]$Steps) {
    $ui.RunSteps.Children.Clear()
    $script:StepRows = @{}
    foreach ($s in $Steps) {
        $tb = New-Object System.Windows.Controls.TextBlock
        $tb.Margin = '0,0,0,10'
        $tb.TextWrapping = 'Wrap'
        $tb.Text = "○  $($StepNames[$s])"
        $tb.Foreground = '#78716C'
        [void]$ui.RunSteps.Children.Add($tb)
        $script:StepRows[$s] = $tb
    }
}

function Set-StepState([string]$Step, [string]$State) {
    $tb = $script:StepRows[$Step]
    if (-not $tb) { return }
    $label = $StepNames[$Step]
    switch ($State) {
        'start'   { $tb.Text = "◐  $label";             $tb.Foreground = '#1F6FEB'; $tb.FontWeight = 'SemiBold' }
        'done'    { $tb.Text = "✓  $label";             $tb.Foreground = '#15803D'; $tb.FontWeight = 'Normal' }
        'failed'  { $tb.Text = "✕  $label (see log)";   $tb.Foreground = '#B91C1C'; $tb.FontWeight = 'Normal' }
        'skipped' { $tb.Text = "–  $label";             $tb.Foreground = '#A8A29E'; $tb.FontWeight = 'Normal' }
    }
}

# ---------- OLD PC ----------
$script:Rows = $null
$script:OldBoxes = $null

function Update-Total {
    $on = @($script:Rows | Where-Object Include)
    $ui.TotalText.Text = "{0} of {1} items · {2}" -f $on.Count, $script:Rows.Count, (Format-Size (($on | Measure-Object Bytes -Sum).Sum))
}

$ui.BtnOld.Add_Click({
    Show-Page 'PageScan'
    $script:OldBoxes = New-StepBoxes $ui.OldSteps $ExportSteps @()
    Start-Engine @('inventory', '-Store', $Store) $null {
        param($k, $d)
        if ($k -eq 'SCAN') {
            $i, $n, $path = $d -split ' ', 3
            $ui.ScanBar.Value = [double]$i / [double]$n
            $ui.ScanText.Text = "Measuring $i of $n · $path"
        }
    } {
        param($code)
        $m = Read-Json "$Store\manifest.json"
        if ($code -ne 0 -or -not $m) { $ui.ScanText.Text = "The scan failed. See $Store\logs."; return }
        $script:Rows = New-Object System.Collections.ObjectModel.ObservableCollection[PlanRow]
        foreach ($p in $m.Plan | Sort-Object Bytes -Descending) {
            $r = New-Object PlanRow
            $r.Path = $p.Path; $r.Why = $p.Why; $r.Bytes = [int64]$p.Bytes; $r.Files = [int64]$p.Files
            $r.add_PropertyChanged({ Update-Total })
            $script:Rows.Add($r)
        }
        $ui.PlanGrid.ItemsSource = $script:Rows
        $ui.PlanGrid.IsEnabled = $true
        $ui.ScanBar.Value = 1
        $ui.ScanText.Text = "Scan done. Untick what must not move."
        $ui.ScanSub.Text = 'Everything is ticked. Big caches you can rebuild are good candidates to untick.'
        if ($m.Certs) { $ui.OldCertPanel.Visibility = 'Visible' }
        $ui.BtnExport.IsEnabled = $true
        Update-Total
    }
})

$ui.BtnExport.Add_Click({
    $off = @($script:Rows | Where-Object { -not $_.Include } | ForEach-Object { $_.Path })
    Write-Json $off "$Store\deselected.json"
    $steps = Get-CheckedSteps $script:OldBoxes
    $certPw = $ui.OldCertPw.Password
    if ($steps -contains 'certs' -and $ui.OldCertPanel.Visibility -eq 'Visible' -and -not $certPw) {
        [System.Windows.MessageBox]::Show('Type a password for the certificates, or untick them.', 'Hopover') | Out-Null
        return
    }
    $a = @('export', '-Store', $Store, '-Only', ((@($steps) + 'none') -join ','))
    Show-Page 'PageRun'
    $ui.RunTitle.Text = 'Preparing the move'
    $ui.RunSub.Text = 'Hopover saves the app list, settings, tasks and WSL here, then shares this PC read-only with your user.'
    New-RunSteps $ExportSteps
    Start-Engine $a $certPw {
        param($k, $d)
        switch ($k) {
            'STEP'  { $s, $st = $d -split ' ', 2; Set-StepState $s $st }
            'SHARE' {
                $pc, $user, $ips = $d -split '\|'
                $od = if ($env:OneDrive) { "OneDrive\Hopover\Hopover.cmd" } else { "\\$pc\hopover-store\tool\Hopover.cmd" }
                $ui.ShareText.Text = "On the new PC, start $od (or open \\$pc\hopover-store\tool and double-click Hopover.cmd). Choose 'This is the NEW PC' and type:`n`n   PC: $pc      IP: $ips`n   User: $user`n`nLeave this PC on until the new PC says Done."
                $ui.SharePanel.Visibility = 'Visible'
            }
        }
    } {
        param($code)
        $ui.RunTitle.Text = switch ($code) { 0 { 'This PC is ready' } 2 { 'Ready, but some steps failed - see the log' } default { 'Something failed - see the log' } }
    }
})

$ui.BtnCleanup.Add_Click({
    Start-Engine @('cleanup', '-Store', $Store) $null $null {
        param($code)
        $ui.ShareText.Text = "Sharing stopped. Delete $Store when the new PC works: it holds Wi-Fi passwords."
    }
})

# ---------- NEW PC ----------
$script:NewBoxes = $null
$script:From = $null
$script:TotalBytes = 0
$script:DoneBytes = 0
$script:CurBytes = 0
$script:RunBytes = 0
$script:CopyStart = $null

$ui.BtnNew.Add_Click({ Show-Page 'PageConnect'; $ui.FromBox.Focus() | Out-Null })

$ui.FromBox.Add_LostFocus({
    if ($ui.FromBox.Text -and -not $ui.UserBox.Text -and $ui.FromBox.Text -notmatch '^\d') { $ui.UserBox.Text = "$($ui.FromBox.Text.Trim())\$env:USERNAME" }
})

$ui.BtnConnect.Add_Click({
    $from = $ui.FromBox.Text.Trim().TrimStart('\')
    if (-not $from) { $ui.ConnectText.Text = 'Type the name or IP of the old PC.'; return }
    $ui.ConnectText.Text = 'Connecting...'
    $ui.ConnectText.Foreground = '#57534E'
    $Window.Dispatcher.Invoke([action] {}, 'Background')
    $manifestPath = "\\$from\hopover-store\manifest.json"
    if (-not (Test-Path $manifestPath) -and $ui.UserBox.Text) {
        & net.exe use "\\$from\IPC$" /delete /y 2>&1 | Out-Null
        $out = & net.exe use "\\$from\IPC$" $ui.PwBox.Password "/user:$($ui.UserBox.Text.Trim())" /persistent:no 2>&1
        if ($LASTEXITCODE -ne 0) {
            $ui.ConnectText.Text = "Login failed: $(($out | Out-String).Trim())"
            $ui.ConnectText.Foreground = '#B91C1C'
            return
        }
    }
    if (-not (Test-Path $manifestPath)) {
        $ui.ConnectText.Text = "Connected, but \\$from\hopover-store is missing. Click 'Prepare the move' on the old PC first."
        $ui.ConnectText.Foreground = '#B91C1C'
        return
    }
    $m = Read-Json $manifestPath
    $script:From = $from
    $apps = Read-Json "\\$from\hopover-store\apps\winget.json"
    $appCount = if ($apps) { @($apps.Sources.Packages).Count } else { 0 }
    $tasks = @(Read-Json "\\$from\hopover-store\tasks\tasks.json" | Where-Object { $_ }).Count
    $script:TotalBytes = [double](($m.Plan | Measure-Object Bytes -Sum).Sum)
    $ui.ConnectText.Text = "Connected to $($m.Computer)."
    $ui.ConnectText.Foreground = '#15803D'
    $ui.SummaryText.Text = "$($m.Computer) ($($m.User)) · $appCount apps · $(@($m.Plan).Count) folders and files, $(Format-Size $script:TotalBytes) · $tasks scheduled tasks · WSL: $((@($m.Distros) | Where-Object { $_ -notmatch '^docker-desktop' }) -join ', ')"
    $state = Read-Json "$Store\state-import.json"
    $done = if ($state) { @($state.Done | Where-Object { $_ -ne 'files' }) } else { @() }
    $script:NewBoxes = New-StepBoxes $ui.NewSteps $ImportSteps $done
    if (Test-Path "\\$from\hopover-store\certs\*.pfx") { $ui.NewCertPanel.Visibility = 'Visible' }
    $ui.ImportPanel.Visibility = 'Visible'
})

function Update-CopyBar {
    $done = [math]::Max($script:DoneBytes, $script:RunBytes)
    if ($script:TotalBytes -gt 0) { $ui.CopyBar.Value = [math]::Min(1, $done / $script:TotalBytes) }
    $txt = "$(Format-Size $done) of $(Format-Size $script:TotalBytes)"
    if ($script:CopyStart -and $script:RunBytes -gt 0) {
        $secs = ([DateTime]::Now - $script:CopyStart).TotalSeconds
        $rate = $script:RunBytes / [math]::Max(1, $secs)
        $left = ($script:TotalBytes - $done) / [math]::Max(1, $rate)
        $txt += " · {0:N0} MB/s · about {1}" -f ($rate / 1MB), $(if ($left -ge 3600) { '{0:N1} h left' -f ($left / 3600) } else { '{0:N0} min left' -f [math]::Max(1, $left / 60) })
    }
    $ui.CopyText.Text = $txt
}

$ui.BtnImport.Add_Click({
    $steps = Get-CheckedSteps $script:NewBoxes
    if (-not $steps) { return }
    $certPw = $ui.NewCertPw.Password
    if ($steps -contains 'certs' -and $ui.NewCertPanel.Visibility -eq 'Visible' -and -not $certPw) {
        [System.Windows.MessageBox]::Show('Type the certificate password from the old PC, or untick the certificates.', 'Hopover') | Out-Null
        return
    }
    $a = @('import', '-From', $script:From, '-Store', $Store, '-Only', ($steps -join ','))
    if ($ui.DryNew.IsChecked) { $a += '-WhatIf' }
    Show-Page 'PageRun'
    $ui.RunTitle.Text = 'Moving to this PC'
    $ui.RunSub.Text = 'Sign in to 1Password and OneDrive while the apps install. You can close Hopover at any time and start it again: it goes on where it stopped.'
    New-RunSteps $ImportSteps
    foreach ($s in $ImportSteps | Where-Object { $steps -notcontains $_ }) { Set-StepState $s 'skipped' }
    $script:DoneBytes = 0; $script:CurBytes = 0; $script:RunBytes = 0; $script:CopyStart = $null
    Start-Engine $a $certPw {
        param($k, $d)
        switch ($k) {
            'STEP' {
                $s, $st = $d -split ' ', 2
                Set-StepState $s $st
                if ($s -eq 'files' -and $st -eq 'start') { $ui.CopyPanel.Visibility = 'Visible'; $script:CopyStart = [DateTime]::Now }
                if ($s -eq 'files' -and $st -eq 'done') { $script:DoneBytes = $script:TotalBytes; Update-CopyBar }
            }
            'ITEM' {
                $i, $n, $bytes, $path = $d -split ' ', 4
                $script:DoneBytes += $script:CurBytes
                $script:CurBytes = [double]$bytes
                $ui.CopyItem.Text = "Item $i of $n · $path"
                Update-CopyBar
            }
            'BYTES' { $script:RunBytes = [double]$d; Update-CopyBar }
        }
    } {
        param($code)
        $ui.RunTitle.Text = switch ($code) {
            0 { 'Done. Now the checklist.' }
            2 { 'Done, but some steps failed. Start Hopover again to retry them, or see the checklist.' }
            default { 'Stopped with an error - see the log, then start again' }
        }
        $ui.RunSub.Text = 'The checklist names what no tool can move: sign-ins, saved Windows logins and licences. Turn on the scheduled tasks when the repos and the Python venvs work.'
        $ui.DonePanel.Visibility = 'Visible'
    }
})

$ui.BtnChecklist.Add_Click({ if (Test-Path "$Store\CHECKLIST.md") { Start-Process notepad.exe "$Store\CHECKLIST.md" } })
$ui.BtnLogs.Add_Click({ Start-Process explorer.exe "$Store\logs" })
$ui.BtnTasks.Add_Click({ Start-Engine @('enable-tasks', '-Store', $Store) $null $null { param($code) Add-Log "enable-tasks finished ($code)" } })
$ui.BtnVerify.Add_Click({ Start-Engine @('verify', '-Store', $Store) $null $null { param($code) Add-Log "verify finished ($code). Gaps: $Store\verify.txt" } })

# ---------- start ----------
if (Test-Path "$Store\state-import.json") { $ui.ResumeNote.Visibility = 'Visible' }
# Smoke test: the window and all named controls load, nothing is shown.
if ($env:HOPOVER_GUI_TEST) { "GUI OK: $($ui.Count) controls, missing: $(@($ui.Keys | Where-Object { -not $ui[$_] }) -join ',')"; return }
if ($SessionUser -and $SessionUser -ne $env:USERNAME) {
    # UAC with another admin's password: this window would move into THAT user's profile.
    [System.Windows.MessageBox]::Show("Hopover runs as $env:USERNAME, but you are signed in as $SessionUser. Make $SessionUser an administrator, sign in again and restart Hopover.", 'Hopover') | Out-Null
    exit 1
}
if (-not (Test-Admin)) {
    [System.Windows.MessageBox]::Show('Start Hopover with Hopover.cmd: it needs administrator rights.', 'Hopover') | Out-Null
    exit 1
}
$Window.Add_Closing({
    if ($script:Proc -and -not $script:Proc.HasExited) {
        $r = [System.Windows.MessageBox]::Show('Hopover is still working. Stop it? You can start again later; it goes on where it stopped.', 'Hopover', 'YesNo')
        if ($r -ne 'Yes') { $_.Cancel = $true; return }
        # robocopy and winget run as children of the engine; stop the whole tree.
        & taskkill.exe /PID $script:Proc.Id /T /F 2>&1 | Out-Null
    }
})
[void]$Window.ShowDialog()
