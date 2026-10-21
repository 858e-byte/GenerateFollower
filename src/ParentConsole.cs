using System.Runtime.InteropServices;

namespace GenerateFollower;

/// <summary>
/// L'exe est une application Windows (pas de console au double-clic). Pour les commandes en ligne (build, scan, lookup…),
/// la sortie va dans la console d'où on les lance, si elle existe ; une sortie déjà redirigée (tests, scripts) est gardée.
/// </summary>
static class ParentConsole
{
    const int AttachParentProcess = -1;
    const int StdOutput = -11;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    static extern nint GetStdHandle(int handle);

    public static void Attach()
    {
        var h = GetStdHandle(StdOutput);
        if (h != 0 && h != -1) return;   // sortie redirigée (fichier, pipe) : rien à faire
        AttachConsole(AttachParentProcess);
    }
}
