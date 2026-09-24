# Sets a known password on a LOCAL account so you can log in to the API during development.
# Hashes through tools/HashPassword (the same Identity-V3-compatible hasher the API uses), writes the hash to the
# catalog Account AND the tenant AspNetUsers row (so the legacy app and the sync job agree). Local servers only.
#
# Usage: .\tools\set-local-password.ps1 -Email superadmin@jaftim.com -Password 'LocalTest1234'
#        [-Server localhost] [-TenantDatabase jaftim-local-db] [-CatalogDatabase jaftim-local-catalog]
param(
    [Parameter(Mandatory)] [string] $Email,
    [Parameter(Mandatory)] [string] $Password,
    [string] $Server = "localhost",
    [string] $TenantDatabase = "jaftim-local-db",
    [string] $CatalogDatabase = "jaftim-local-catalog"
)
$ErrorActionPreference = 'Stop'

if ($Server -notmatch '^(localhost|\(local\)|127\.0\.0\.1|\.)(\\.*)?$') {
    throw "Refusing to run against '$Server' - this tool is for local databases only."
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
# No extra dotnet options here: `dotnet run` forwards anything it does not recognise (e.g. -nologo) to the app as arguments.
$hash = (& dotnet run --project (Join-Path $root 'tools\HashPassword') -- $Password | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $hash -notmatch '^[A-Za-z0-9+/]{80,}={0,2}$') {
    throw "Hashing failed (exit $LASTEXITCODE): '$hash'"
}

$safeEmail = $Email.Replace("'", "''")
$tenantSql = "UPDATE dbo.AspNetUsers SET PasswordHash = '$hash', LockoutEnd = NULL, AccessFailedCount = 0 WHERE NormalizedEmail = UPPER(N'$safeEmail'); SELECT @@ROWCOUNT AS tenant_rows;"
$catalogSql = "UPDATE dbo.Account SET PasswordHash = '$hash', LockoutEnd = NULL, AccessFailedCount = 0, PasswordChangedByApiAtUtc = NULL WHERE NormalizedEmail = UPPER(N'$safeEmail'); SELECT @@ROWCOUNT AS catalog_rows;"

& sqlcmd -S $Server -E -d $TenantDatabase -b -Q $tenantSql -W
if ($LASTEXITCODE -ne 0) { throw "tenant update failed" }
& sqlcmd -S $Server -E -d $CatalogDatabase -b -Q $catalogSql -W
if ($LASTEXITCODE -ne 0) { throw "catalog update failed (has the catalog been seeded by AccountSyncJob yet?)" }
