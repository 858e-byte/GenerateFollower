using System.IO.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace GenerateFollower;

/// <summary>
/// Système de fichiers donné à Mutagen : les fichiers ouverts en lecture le sont avec le partage « suppression » et
/// « écriture ». Sans cela, Mutagen garde chaque plugin ouvert tant que le Builder tourne, et Vortex (ou MO2) ne peut
/// pas retirer ni remplacer un plugin (lien dur supprimé puis recréé au déploiement).
/// MO2 lu directement (GameFiles) : un chemin de Data est redirigé vers le fichier gagnant (plugin, BSA, Strings dans
/// un mod de MO2), et les listings de Data montrent aussi les fichiers des mods, sous leur chemin dans Data.
/// </summary>
public sealed class SharedFileSystem : FileSystem
{
    readonly IFile file;
    readonly IFileStreamFactory streams;
    readonly IFileInfoFactory infos;
    readonly IDirectory directory;
    readonly GameFiles? files;

    public SharedFileSystem(GameFiles? files = null)
    {
        this.files = files is { Direct: true } ? files : null;
        file = new SharedFile(this);
        streams = new SharedStreams(this, base.FileStream);
        infos = new RedirectInfos(this, base.FileInfo);
        directory = new RedirectDirectory(this);
    }

    public override IFile File => file;
    public override IFileStreamFactory FileStream => streams;
    public override IFileInfoFactory FileInfo => infos;
    public override IDirectory Directory => directory;

    /// <summary>Chemin réel d'un chemin de Data (MO2 direct), sinon le chemin tel quel.</summary>
    string Real(string path) => files?.Redirect(path) ?? path;

    /// <summary>Partage élargi pour une ouverture en lecture seule (rien ne change pour une écriture).</summary>
    static FileShare Widen(FileAccess access, FileShare share) =>
        access == FileAccess.Read ? share | FileShare.Delete | FileShare.ReadWrite : share;

    sealed class SharedFile(SharedFileSystem fs) : FileWrapper(fs)
    {
        public override bool Exists(string? path) => path != null && base.Exists(fs.Real(path));
        public override DateTime GetLastWriteTime(string path) => base.GetLastWriteTime(fs.Real(path));
        public override DateTime GetLastWriteTimeUtc(string path) => base.GetLastWriteTimeUtc(fs.Real(path));
        public override byte[] ReadAllBytes(string path) { using var s = OpenRead(path); using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); }
        public override FileSystemStream OpenRead(string path) => FileSystem.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        public override FileSystemStream Open(string path, FileMode mode) => FileSystem.FileStream.New(path, mode);
        public override FileSystemStream Open(string path, FileMode mode, FileAccess access) => FileSystem.FileStream.New(path, mode, access);
        public override FileSystemStream Open(string path, FileMode mode, FileAccess access, FileShare share) => FileSystem.FileStream.New(path, mode, access, share);
    }

    sealed class SharedStreams(SharedFileSystem fs, IFileStreamFactory inner) : IFileStreamFactory
    {
        public IFileSystem FileSystem => fs;
        static bool Reading(FileMode mode, FileAccess access) => access == FileAccess.Read && mode == FileMode.Open;
        string Path(string path, FileMode mode, FileAccess access) => Reading(mode, access) ? fs.Real(path) : path;
        public FileSystemStream New(SafeFileHandle handle, FileAccess access) => inner.New(handle, access);
        public FileSystemStream New(SafeFileHandle handle, FileAccess access, int bufferSize) => inner.New(handle, access, bufferSize);
        public FileSystemStream New(SafeFileHandle handle, FileAccess access, int bufferSize, bool isAsync) => inner.New(handle, access, bufferSize, isAsync);
        public FileSystemStream New(string path, FileMode mode)
            => New(path, mode, mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite, FileShare.Read);
        public FileSystemStream New(string path, FileMode mode, FileAccess access)
            => New(path, mode, access, FileShare.Read);
        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share)
            => inner.New(Path(path, mode, access), mode, access, Widen(access, share));
        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize)
            => inner.New(Path(path, mode, access), mode, access, Widen(access, share), bufferSize);
        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, bool useAsync)
            => inner.New(Path(path, mode, access), mode, access, Widen(access, share), bufferSize, useAsync);
        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options)
            => inner.New(Path(path, mode, access), mode, access, Widen(access, share), bufferSize, options);
        public FileSystemStream New(string path, FileStreamOptions options)
        {
            options.Share = Widen(options.Access, options.Share);
            return inner.New(Path(path, options.Mode, options.Access), options);
        }
        public FileSystemStream Wrap(FileStream fileStream) => inner.Wrap(fileStream);
    }

    /// <summary>FileInfo d'un chemin de Data : celui du fichier gagnant (taille, date, existence).</summary>
    sealed class RedirectInfos(SharedFileSystem fs, IFileInfoFactory inner) : IFileInfoFactory
    {
        public IFileSystem FileSystem => fs;
        public IFileInfo New(string fileName) => inner.New(fs.Real(fileName));
        public IFileInfo? Wrap(FileInfo? fileInfo) => inner.Wrap(fileInfo);
    }

    /// <summary>Listings de Data (BSA, Strings…) : fichiers de toutes les racines de MO2, sous leur chemin dans Data.</summary>
    sealed class RedirectDirectory(SharedFileSystem fs) : DirectoryWrapper(fs)
    {
        IEnumerable<string>? Merged(string path, string pattern, SearchOption option) => fs.files?.ListAsData(path, pattern, option == SearchOption.AllDirectories);
        public override bool Exists(string? path) => base.Exists(path) || (path != null && fs.files?.ListAsData(path, "*", false) is { } l && l.Any());
        public override IEnumerable<string> EnumerateFiles(string path) => Merged(path, "*", SearchOption.TopDirectoryOnly) ?? base.EnumerateFiles(path);
        public override IEnumerable<string> EnumerateFiles(string path, string searchPattern) => Merged(path, searchPattern, SearchOption.TopDirectoryOnly) ?? base.EnumerateFiles(path, searchPattern);
        public override IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption) => Merged(path, searchPattern, searchOption) ?? base.EnumerateFiles(path, searchPattern, searchOption);
        public override string[] GetFiles(string path) => Merged(path, "*", SearchOption.TopDirectoryOnly)?.ToArray() ?? base.GetFiles(path);
        public override string[] GetFiles(string path, string searchPattern) => Merged(path, searchPattern, SearchOption.TopDirectoryOnly)?.ToArray() ?? base.GetFiles(path, searchPattern);
        public override string[] GetFiles(string path, string searchPattern, SearchOption searchOption) => Merged(path, searchPattern, searchOption)?.ToArray() ?? base.GetFiles(path, searchPattern, searchOption);
    }
}
