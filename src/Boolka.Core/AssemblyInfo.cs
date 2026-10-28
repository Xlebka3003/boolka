using System.Runtime.CompilerServices;

// UI-проекты получают доступ к внутренним моделям, не превращая их в публичный API.
[assembly: InternalsVisibleTo("boolka")]
[assembly: InternalsVisibleTo("Boolka.Desktop")]
[assembly: InternalsVisibleTo("Boolka.Verification")]
