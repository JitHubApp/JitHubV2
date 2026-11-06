# The resvg supply-chain inventory currently needs only this bounded subset of
# SPDX expressions. Unsupported SPDX constructs fail closed instead of being
# partially accepted by token matching.
$script:SpdxSelectionMaximumExpressionLength = 1024
$script:SpdxSelectionMaximumTokens = 128
$script:SpdxSelectionMaximumDepth = 16
$script:SpdxSelectionMaximumNodes = 128

function Get-ResvgAdditionalKnownLicenseIdentifiers {
    param(
        [Parameter(Mandatory)][string[]] $ApprovedIdentifiers,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $InventoryIdentifiers
    )

    # 0BSD is present only in an unselected alternative in the pinned Cargo
    # closure. Recognizing it is not permission to select it. The generated
    # inventory cannot grant recognition to arbitrary new license identifiers.
    foreach ($identifier in $InventoryIdentifiers) {
        if ($identifier -cnotin $ApprovedIdentifiers -and $identifier -cne '0BSD') {
            throw "The native inventory contains an unreviewed additional license identifier '$identifier'."
        }
    }
    return ,@('0BSD')
}

function Get-SpdxSelectionTokens {
    param([Parameter(Mandatory)][string] $Expression)

    if ($Expression.Length -gt $script:SpdxSelectionMaximumExpressionLength) {
        throw 'SPDX expression exceeds the configured length limit.'
    }
    if ([string]::IsNullOrWhiteSpace($Expression)) {
        throw 'SPDX expression is empty.'
    }

    $tokens = [Collections.Generic.List[object]]::new()
    $position = 0
    while ($position -lt $Expression.Length) {
        $character = $Expression[$position]
        if ([char]::IsWhiteSpace($character)) {
            $position++
            continue
        }

        $start = $position
        if ($character -ceq '(' -or $character -ceq ')') {
            $position++
            $value = [string]$character
        }
        elseif (($character -ge 'A' -and $character -le 'Z') -or
            ($character -ge 'a' -and $character -le 'z') -or
            ($character -ge '0' -and $character -le '9')) {
            $position++
            while ($position -lt $Expression.Length) {
                $next = $Expression[$position]
                if (($next -ge 'A' -and $next -le 'Z') -or
                    ($next -ge 'a' -and $next -le 'z') -or
                    ($next -ge '0' -and $next -le '9') -or
                    $next -ceq '.' -or $next -ceq '-') {
                    $position++
                    continue
                }
                break
            }
            $value = $Expression.Substring($start, $position - $start)
        }
        else {
            throw "SPDX expression contains an unsupported character at offset $position."
        }

        $tokens.Add([pscustomobject]@{ Value = $value; Offset = $start })
        if ($tokens.Count -gt $script:SpdxSelectionMaximumTokens) {
            throw 'SPDX expression exceeds the configured token limit.'
        }
    }

    return ,$tokens.ToArray()
}

function Read-SpdxSelectionPrimary {
    param(
        [Parameter(Mandatory)] $State,
        [int] $Depth = 0
    )

    if ($State.Index -ge $State.Tokens.Count) {
        throw 'SPDX expression ends where a license identifier was required.'
    }

    $token = $State.Tokens[$State.Index]
    if ($token.Value -ceq '(') {
        $nestedDepth = $Depth + 1
        if ($nestedDepth -gt $script:SpdxSelectionMaximumDepth) {
            throw 'SPDX expression exceeds the configured nesting limit.'
        }
        $State.Index++
        $node = Read-SpdxSelectionOr -State $State -Depth $nestedDepth
        if ($State.Index -ge $State.Tokens.Count -or $State.Tokens[$State.Index].Value -cne ')') {
            throw "SPDX expression has an unclosed parenthesis at offset $($token.Offset)."
        }
        $State.Index++
        return $node
    }

    if ($token.Value -ceq ')' -or $token.Value -ceq 'AND' -or $token.Value -ceq 'OR') {
        throw "SPDX expression has an unexpected '$($token.Value)' at offset $($token.Offset)."
    }

    if (-not $State.KnownIdentifiers.Contains($token.Value)) {
        throw "SPDX expression contains an unknown or unapproved identifier '$($token.Value)'."
    }

    $State.Index++
    $State.NodeCount++
    if ($State.NodeCount -gt $script:SpdxSelectionMaximumNodes) {
        throw 'SPDX expression exceeds the configured node limit.'
    }
    return [pscustomobject]@{ Kind = 'Identifier'; Value = $token.Value; Left = $null; Right = $null }
}

function Read-SpdxSelectionAnd {
    param(
        [Parameter(Mandatory)] $State,
        [int] $Depth = 0
    )

    $node = Read-SpdxSelectionPrimary -State $State -Depth $Depth
    while ($State.Index -lt $State.Tokens.Count -and $State.Tokens[$State.Index].Value -ceq 'AND') {
        $State.Index++
        $right = Read-SpdxSelectionPrimary -State $State -Depth $Depth
        $State.NodeCount++
        if ($State.NodeCount -gt $script:SpdxSelectionMaximumNodes) {
            throw 'SPDX expression exceeds the configured node limit.'
        }
        $node = [pscustomobject]@{ Kind = 'And'; Value = $null; Left = $node; Right = $right }
    }
    return $node
}

function Read-SpdxSelectionOr {
    param(
        [Parameter(Mandatory)] $State,
        [int] $Depth = 0
    )

    $node = Read-SpdxSelectionAnd -State $State -Depth $Depth
    while ($State.Index -lt $State.Tokens.Count -and $State.Tokens[$State.Index].Value -ceq 'OR') {
        if (-not $State.AllowOr) {
            throw 'A selected license must be expressed as a conjunction, not an alternative.'
        }
        $State.Index++
        $right = Read-SpdxSelectionAnd -State $State -Depth $Depth
        $State.NodeCount++
        if ($State.NodeCount -gt $script:SpdxSelectionMaximumNodes) {
            throw 'SPDX expression exceeds the configured node limit.'
        }
        $node = [pscustomobject]@{ Kind = 'Or'; Value = $null; Left = $node; Right = $right }
    }
    return $node
}

function ConvertFrom-BoundedSpdxExpression {
    param(
        [Parameter(Mandatory)][string] $Expression,
        [Parameter(Mandatory)][Collections.Generic.HashSet[string]] $KnownIdentifiers,
        [switch] $AllowOr
    )

    $tokens = Get-SpdxSelectionTokens -Expression $Expression
    $state = [pscustomobject]@{
        Tokens = $tokens
        Index = 0
        NodeCount = 0
        KnownIdentifiers = $KnownIdentifiers
        AllowOr = [bool]$AllowOr
    }
    $root = Read-SpdxSelectionOr -State $state
    if ($state.Index -ne $tokens.Count) {
        $token = $tokens[$state.Index]
        throw "SPDX expression has trailing or malformed input at offset $($token.Offset)."
    }
    return $root
}

function Add-SpdxSelectionIdentifiers {
    param(
        [Parameter(Mandatory)] $Node,
        [Parameter(Mandatory)] $Identifiers
    )

    if ($Node.Kind -ceq 'Identifier') {
        [void]$Identifiers.Add([string]$Node.Value)
        return
    }
    Add-SpdxSelectionIdentifiers -Node $Node.Left -Identifiers $Identifiers
    Add-SpdxSelectionIdentifiers -Node $Node.Right -Identifiers $Identifiers
}

function Test-SpdxSelectionExpression {
    param(
        [Parameter(Mandatory)] $Node,
        [Parameter(Mandatory)][Collections.Generic.HashSet[string]] $SelectedIdentifiers
    )

    if ($Node.Kind -ceq 'Identifier') {
        return $SelectedIdentifiers.Contains([string]$Node.Value)
    }

    # Evaluate both operands explicitly. Parsing already validates the whole
    # expression; avoiding PowerShell short-circuit operators here keeps that
    # property obvious if diagnostics are added later.
    $left = Test-SpdxSelectionExpression -Node $Node.Left -SelectedIdentifiers $SelectedIdentifiers
    $right = Test-SpdxSelectionExpression -Node $Node.Right -SelectedIdentifiers $SelectedIdentifiers
    if ($Node.Kind -ceq 'And') { return [bool]($left -and $right) }
    if ($Node.Kind -ceq 'Or') { return [bool]($left -or $right) }
    throw 'SPDX expression contains an unsupported internal node.'
}

function Assert-SpdxLicenseSelection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Expression,
        [Parameter(Mandatory)][string] $Selection,
        [Parameter(Mandatory)][string[]] $ApprovedIdentifiers,
        [string[]] $AdditionalKnownIdentifiers = @(),
        [Parameter(Mandatory)][string] $Subject
    )

    # Cargo's historical spelling is accepted only as this exact metadata
    # value. All other slash-separated forms remain unsupported.
    $normalizedExpression = if ($Expression -ceq 'MIT/Apache-2.0') {
        'MIT OR Apache-2.0'
    }
    else {
        $Expression
    }

    $approved = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($identifier in $ApprovedIdentifiers) {
        if ([string]::IsNullOrWhiteSpace($identifier)) { throw 'The approved SPDX identifier list is invalid.' }
        [void]$approved.Add($identifier)
    }
    if ($approved.Count -eq 0) { throw 'No approved SPDX identifiers were supplied.' }

    $known = [Collections.Generic.HashSet[string]]::new($approved, [StringComparer]::Ordinal)
    foreach ($identifier in $AdditionalKnownIdentifiers) {
        if ([string]::IsNullOrWhiteSpace($identifier)) { throw 'The additional SPDX identifier list is invalid.' }
        [void]$known.Add($identifier)
    }

    $declaredAst = ConvertFrom-BoundedSpdxExpression -Expression $normalizedExpression -KnownIdentifiers $known -AllowOr
    $selectedAst = ConvertFrom-BoundedSpdxExpression -Expression $Selection -KnownIdentifiers $approved

    $offered = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    Add-SpdxSelectionIdentifiers -Node $declaredAst -Identifiers $offered
    $selected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $selectedLeaves = [Collections.Generic.List[string]]::new()
    Add-SpdxSelectionIdentifiers -Node $selectedAst -Identifiers $selected
    Add-SpdxSelectionIdentifiers -Node $selectedAst -Identifiers $selectedLeaves
    if ($selected.Count -ne $selectedLeaves.Count) {
        throw "Selected license for $Subject repeats an identifier."
    }
    foreach ($identifier in $selected) {
        if (-not $offered.Contains($identifier)) {
            throw "Selected license '$identifier' is not offered by $Subject."
        }
    }
    if (-not (Test-SpdxSelectionExpression -Node $declaredAst -SelectedIdentifiers $selected)) {
        throw "Selected license conjunction does not satisfy the declared SPDX expression for $Subject."
    }

    return [pscustomobject]@{
        NormalizedExpression = $normalizedExpression
        SelectedIdentifiers = @($selectedLeaves.ToArray())
    }
}
