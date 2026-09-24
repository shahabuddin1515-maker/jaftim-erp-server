// Usage: HashPassword <password>            -> prints an ASP.NET-Identity-V3-compatible hash (stdout, no newline)
//        HashPassword <password> <hash>     -> prints Success / SuccessRehashNeeded / Failed for that pair
// Used by tools/set-local-password.ps1. Exit codes: 0 ok, 1 self-verification failed, 2 usage.
using Jaftim.Application.Abstractions;
using Jaftim.Infrastructure.Security;

if (args.Length is 0 or > 2 || args[0].Length == 0 || args[0].StartsWith('-')) { Console.Error.WriteLine("usage: HashPassword <password> [<hash>]  (got " + args.Length + " args)"); return 2; }

var hasher = new IdentityCompatiblePasswordHasher();
if (args.Length == 2)
{
    Console.Write(hasher.Verify(args[1], args[0]));
    return 0;
}

string hash = hasher.Hash(args[0]);
if (hasher.Verify(hash, args[0]) != PasswordVerificationResult.Success) { Console.Error.WriteLine("self-verification failed"); return 1; }
Console.Write(hash);
return 0;
