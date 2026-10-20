using System.IO.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace GenerateFollower;

/// <summary>
/// Système de fichiers donné à Mutagen : les fichiers ouverts en lecture le sont avec le partage « suppression » et
/// « écriture ». Sans cela, Mutagen garde chaque plugin ouvert tant que le Builder tourne, et Vortex (ou MO2) ne peut
/// pas retirer ni remplacer un plugin (lien dur supprimé puis recréé au déploiement).
/// </summary>
public sealed class SharedFileSystem : FileSystem
{
    readonly IFile file;
    readonly IFileStreamFactory streams;

    public SharedFileSystem()
    {
        file = new SharedFile(this);
        streams = new SharedStreams(this, base.FileStream);
    }

    public override IFile File => file;
    public override IFileStreamFactory FileStream => streams;

    /// <summary>Partage élargi pour une ouverture en lecture seule (rien ne change pour une écriture).</summary>
    static FileShare Widen(FileAccess access, FileShare share) =>
        access == FileAccess.Read ? share | FileShare.Delete | FileShare.ReadWrite : share;

    sealed class SharedFile(IFileSystem fs) : FileWrapper(fs)
    {
        public override FileSystemStream OpenRead(string path) => FileSystem.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        public override FileSystemStream Open(string path, FileMode mode) => FileSystem.FileStream.New(path, mode);
        public override FileSystemStream Open(string path, FileMode mode, FileAccess access) => FileSystem.FileStream.New(path, mode, access);
        public override FileSystemStream Open(string path, FileMode mode, FileAccess access, FileShare share) => FileSystem.FileStream.New(path, mode, access, share);
    }

    sealed class SharedStreams(IFileSystem fs, IFileStreamFactory inner) : IFileStreamFactory
    {
        public IFileSystem FileSystem => fs;
        public FileSystemStream New(SafeFileHandle handle, FileAccess access) => inner.New(handle, access);
        public FileSystemStream New(SafeFileHandle handle, FileAccess access, int bufferSize) => inner.New(handle, access, bufferSize);
        public FileSystemStream New(SafeFileHandle handle, FileAccess access, int bufferSize, bool isAsync) => inner.New(handle, access, bufferSize, isAsync);
        public FileSystemStream New(string path, FileMode mode)
            => New(path, mode, mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite, FileShare.Read);
        public FileSystemStream New(string path, FileMode mode, FileAccess access)
            => New(path, mode, access, FileShare.Read);
        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share)
            => inner.New(path, mode, access, Widen(access, share));
        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize)
            => inner.New(path, mode, access, Widen(access, share), bufferSize);
        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, bool useAsync)
            => inner.New(path, mode, access, Widen(access, share), bufferSize, useAsync);
        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options)
            => inner.New(path, mode, access, Widen(access, share), bufferSize, options);
        public FileSystemStream New(string path, FileStreamOptions options)
        {
            options.Share = Widen(options.Access, options.Share);
            return inner.New(path, options);
        }
        public FileSystemStream Wrap(FileStream fileStream) => inner.Wrap(fileStream);
    }
}
