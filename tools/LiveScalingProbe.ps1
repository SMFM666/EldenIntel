Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class EIReadMemory {
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
  [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
}
'@

$process = Get-Process -Name eldenring -ErrorAction Stop | Select-Object -First 1
$module = $process.Modules | Where-Object ModuleName -eq 'eldenring.exe' | Select-Object -First 1
$handle = [EIReadMemory]::OpenProcess(0x0010, $false, $process.Id)
if ($handle -eq [IntPtr]::Zero) { throw 'Could not open Elden Ring read-only.' }

function Read-Bytes([uint64]$address, [int]$count) {
  $bytes = [byte[]]::new($count); $read = [IntPtr]::Zero
  $pointer = [IntPtr]::new([long]$address)
  if (-not [EIReadMemory]::ReadProcessMemory($handle, $pointer, $bytes, $count, [ref]$read) -or $read.ToInt64() -ne $count) { return $null }
  return $bytes
}
function Read-U64([uint64]$address) { $b=Read-Bytes $address 8; if($null -eq $b){return [uint64]0}; return [BitConverter]::ToUInt64($b,0) }
function Read-I32([uint64]$address) { $b=Read-Bytes $address 4; if($null -eq $b){return 0}; return [BitConverter]::ToInt32($b,0) }
function Read-U32([uint64]$address) { $b=Read-Bytes $address 4; if($null -eq $b){return [uint32]0}; return [BitConverter]::ToUInt32($b,0) }
function Is-Ptr([uint64]$value) { return $value -ge 0x10000 -and $value -lt 0x0000800000000000 }

try {
  $base=[uint64]$module.BaseAddress.ToInt64(); $world=Read-U64 ($base+0x3D65F88)
  $players=Read-U64 ($world+0x10EF8); $player=Read-U64 $players
  $pgd=Read-U64 ($player+0x580); $pm=Read-U64 ($player+0x190); $pd=Read-U64 $pm
  $level=Read-U32 ($pgd+0x68); $vigor=Read-U32 ($pgd+0x3C); $reportedMax=Read-U32 ($pgd+0x14)
  $playerHp=Read-I32 ($pd+0x138); $playerMax=Read-I32 ($pd+0x13C)
  $target=[math]::Round($reportedMax*6.5+$level*25)
  Write-Output "PLAYER RL=$level VIG=$vigor HP=$playerHp/$playerMax PROGRESSION_MAX=$reportedMax AUTO_TARGET_HP=$target"
  $known=@{}; $baselineSamples=15
  for($sample=0;$sample -lt 300;$sample++) {
    $begin=Read-U64 ($world+0x1F1B8); $end=Read-U64 ($world+0x1F1C0)
    if((Is-Ptr $begin) -and $end -ge $begin -and ($end-$begin) -le 800000) {
      for($cursor=$begin;$cursor -lt $end;$cursor+=8) {
        $character=Read-U64 $cursor
        if(-not (Is-Ptr $character) -or $character -eq $player){continue}
        $modules=Read-U64 ($character+0x190); if(-not (Is-Ptr $modules)){continue}
        $data=Read-U64 $modules; if(-not (Is-Ptr $data)){continue}
        $hp=Read-I32 ($data+0x138); $max=Read-I32 ($data+0x13C)
        if($max -lt 1 -or $max -gt 10000000 -or $hp -lt 0 -or $hp -gt $max){continue}
        $key=('0x{0:X}' -f $character)
        if(-not $known.ContainsKey($key)) {
          $known[$key]=$max
          if($sample -ge $baselineSamples) {
            $verdict = if([math]::Abs($max-$target) -le 2){'AUTO_SCALED'}else{'NOT_AUTO_TARGET'}
            Write-Output "NEW $key HP=$hp/$max target=$target verdict=$verdict"
          }
        } elseif($known[$key] -ne $max) {
          Write-Output "MAX_CHANGE $key $($known[$key]) -> $max"
          $known[$key]=$max
        }
      }
    }
    Start-Sleep -Milliseconds 200
  }
} finally { [void][EIReadMemory]::CloseHandle($handle) }
