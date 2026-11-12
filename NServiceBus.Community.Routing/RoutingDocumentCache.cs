using System.IO;

namespace NServiceBus.Community.Routing
{
    /// <summary>
    /// Stores the last routing document an endpoint received so that it can start when the router is unreachable.
    /// </summary>
    public interface IRoutingDocumentCache
    {
        string Load();
        void Save(string document);
    }

    public static class RoutingDocumentCache
    {
        /// <summary>
        /// Caches the routing document in a file.
        /// </summary>
        public static IRoutingDocumentCache FileSystem(string directory, string fileName = "routing-document.json")
        {
            return new FileSystemRoutingDocumentCache(Path.Combine(directory, fileName));
        }
    }

    class FileSystemRoutingDocumentCache : IRoutingDocumentCache
    {
        readonly string path;

        public FileSystemRoutingDocumentCache(string path)
        {
            this.path = path;
        }

        public string Load()
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        public void Save(string document)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, document);
            File.Move(temporaryPath, path, true);
        }
    }
}
