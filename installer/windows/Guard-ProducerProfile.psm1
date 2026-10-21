#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Reflection.Metadata

# Read-only build observation, NOT coordinator admission policy or identity proof.
# Candidate profile derived from runtime v9.0.21 DebugHeader.cpp:
# https://raw.githubusercontent.com/dotnet/runtime/v9.0.21/src/coreclr/nativeaot/Runtime/DebugHeader.cpp
# Its presence in actual Windows output remains an observation, not a source assumption.
function Test-HermesGuardProducerProfile {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [System.Reflection.PortableExecutable.PEReader]$Image
    )

    function Require-Profile([bool]$Condition) {
        if (-not $Condition) {
            throw [BadImageFormatException]::new('Closed producer profile mismatch.')
        }
    }

    function Get-ProfileSection([long]$Rva, [int]$Count) {
        Require-Profile ($Rva -gt 0 -and $Rva -le [int]::MaxValue -and $Count -gt 0 `
            -and $Count -le $imageLength -and $Rva + $Count -le $optional.SizeOfImage)
        $sections = @($headers.SectionHeaders | Where-Object {
            $extent = [Math]::Max([long]$_.VirtualSize, [long]$_.SizeOfRawData)
            $Rva -lt [long]$_.VirtualAddress + $extent `
                -and $Rva + $Count -gt [long]$_.VirtualAddress
        })
        Require-Profile ($sections.Count -eq 1)
        $section = $sections[0]
        $delta = $Rva - [long]$section.VirtualAddress
        $backed = [Math]::Min([long]$section.VirtualSize, [long]$section.SizeOfRawData)
        $offset = [long]$section.PointerToRawData + $delta
        Require-Profile ($delta -ge 0 -and $delta + $Count -le $backed `
            -and ([long]$section.SectionCharacteristics -band 0x40000000) -ne 0 `
            -and $section.PointerToRawData -ge $optional.SizeOfHeaders `
            -and $offset + $Count -le $imageLength)
        return $section
    }

    function Read-ProfileBytes([long]$Rva, [int]$Count) {
        $null = Get-ProfileSection $Rva $Count
        $content = $Image.GetSectionData([int]$Rva).GetContent(0, $Count)
        return ,([byte[]]$content)
    }

    function Read-ProfileUInt32([long]$Rva) {
        return [BitConverter]::ToUInt32((Read-ProfileBytes $Rva 4), 0)
    }

    function Read-ProfileUInt16([long]$Rva) {
        return [BitConverter]::ToUInt16((Read-ProfileBytes $Rva 2), 0)
    }

    try {
        # A direct PowerShell property read can hide the getter's exception.
        # Method invocation preserves it through the existing wrapper filter.
        $headersProperty = $Image.GetType().GetProperty('PEHeaders')
        $headers = $headersProperty.GetValue($Image)
        $optional = $headers.PEHeader
        Require-Profile ($null -ne $optional `
            -and $headers.CoffHeader.Machine -eq [System.Reflection.PortableExecutable.Machine]::Amd64 `
            -and ([int]$headers.CoffHeader.Characteristics -band 0x2002) -eq 0x2002 `
            -and $null -eq $headers.CorHeader `
            -and $optional.Magic -eq [System.Reflection.PortableExecutable.PEMagic]::PE32Plus `
            -and $optional.CorHeaderTableDirectory.RelativeVirtualAddress -eq 0 `
            -and $optional.CorHeaderTableDirectory.Size -eq 0)
        $imageLength = $Image.GetEntireImage().Length
        Require-Profile ($imageLength -gt 0 -and $imageLength -le 32 * 1024 * 1024)
        $directory = $optional.ExportTableDirectory
        $start = [long]$directory.RelativeVirtualAddress
        $end = $start + $directory.Size
        Require-Profile ($start -gt 0 -and $directory.Size -ge 40)
        $null = Read-ProfileBytes $start $directory.Size
        Require-Profile ((Read-ProfileUInt32 ($start + 20)) -eq 3 `
            -and (Read-ProfileUInt32 ($start + 24)) -eq 3)
        $functions = Read-ProfileUInt32 ($start + 28)
        $names = Read-ProfileUInt32 ($start + 32)
        $ordinals = Read-ProfileUInt32 ($start + 36)
        $addresses = @(for ($index = 0; $index -lt 3; $index++) {
            Read-ProfileUInt32 ([long]$functions + $index * 4)
        })
        $indexes = @(for ($index = 0; $index -lt 3; $index++) {
            Read-ProfileUInt16 ([long]$ordinals + $index * 2)
        })
        Require-Profile (@($addresses | Select-Object -Unique).Count -eq 3)
        Require-Profile (($indexes | Sort-Object) -join ',' -ceq '0,1,2')

        # PE name pointers are sorted; EAT slots need not be in name order.
        $expectedNames = @('DotNetRuntimeDebugHeader', 'HermesPreflight', 'HermesTransactionFence')
        for ($index = 0; $index -lt 3; $index++) {
            $nameRva = Read-ProfileUInt32 ([long]$names + $index * 4)
            $expected = [Text.Encoding]::ASCII.GetBytes($expectedNames[$index] + [char]0)
            $actual = Read-ProfileBytes $nameRva $expected.Length
            Require-Profile ([Convert]::ToHexString($actual) -ceq [Convert]::ToHexString($expected))
            $target = [long]$addresses[$indexes[$index]]
            $width = if ($index -eq 0) { 32 } else { 1 }
            Require-Profile ($target + $width -le $start -or $target -ge $end)
            $section = Get-ProfileSection $target $width
            $flags = [long]$section.SectionCharacteristics
            if ($index -eq 0) {
                # Writable initialized data; never executable or code.
                Require-Profile (($flags -band 3221225536L) -eq 3221225536L `
                    -and ($flags -band 0x20000020) -eq 0 -and $target % 8 -eq 0)
                # DNDH, major=4, minor=0, x64/little-endian flags=1,
                # reserved=0, DebugTypeEntries=null, GlobalEntries=null on disk.
                Require-Profile ([Convert]::ToHexString((Read-ProfileBytes $target 32)) -ceq `
                    '444E444804000000010000000000000000000000000000000000000000000000')
            } else {
                Require-Profile (($flags -band 0x20000020) -eq 0x20000020)
                $null = Read-ProfileBytes $target 1
            }
        }
        return $true
    } catch {
        # PowerShell wraps .NET method/property failures. Unwrap only those
        # transparent invocation wrappers, never arbitrary exception chains.
        $failure = $_.Exception
        while (($null -ne $failure.InnerException) -and (
            ($failure -is [Management.Automation.MethodInvocationException]) -or
            ($failure -is [Management.Automation.GetValueInvocationException]) -or
            ($failure -is [Reflection.TargetInvocationException])
        )) {
            $failure = $failure.InnerException
        }
        if (($failure -is [BadImageFormatException]) -or ($failure -is [IO.IOException])) {
            # Expected mismatch/malformed-image/read failure: disclose no payload.
            return $false
        }
        # Preserve unexpected failures and cancellation without logging them here.
        throw
    }
}

Export-ModuleMember -Function Test-HermesGuardProducerProfile
